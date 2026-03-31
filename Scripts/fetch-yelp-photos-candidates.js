#!/usr/bin/env node
/**
 * Fetches photos from Yelp for new candidate places, uploads to R2, and updates via API.
 * All-in-one: Yelp search → R2 upload → API PATCH.
 *
 * Usage: node fetch-yelp-photos-candidates.js [candidates-json]
 *   Default: candidates-YYYY-MM-DD.json (today's date)
 *
 * Required env vars:
 *   YELP_API_KEY, R2_ACCOUNT_ID, R2_ACCESS_KEY_ID, R2_SECRET_ACCESS_KEY, ADMIN_API_KEY
 */

const https = require('https');
const fs = require('fs');
const path = require('path');
const sharp = require('sharp');
const { S3Client, PutObjectCommand } = require('@aws-sdk/client-s3');

// --- Config ---
const YELP_API_KEY = process.env.YELP_API_KEY;
const R2_ACCOUNT_ID = process.env.R2_ACCOUNT_ID;
const R2_BUCKET = process.env.R2_BUCKET || 'locallist-images';
const R2_PUBLIC_URL = process.env.R2_PUBLIC_URL || 'https://pub-7f09e69b5b644703825b6068a05dee8f.r2.dev';
const API_URL = 'https://locallist-api-net-production.up.railway.app';
const ADMIN_KEY = process.env.ADMIN_API_KEY;

// --- Validate env ---
const missing = [];
if (!YELP_API_KEY) missing.push('YELP_API_KEY');
if (!R2_ACCOUNT_ID) missing.push('R2_ACCOUNT_ID');
if (!process.env.R2_ACCESS_KEY_ID) missing.push('R2_ACCESS_KEY_ID');
if (!process.env.R2_SECRET_ACCESS_KEY) missing.push('R2_SECRET_ACCESS_KEY');
if (!ADMIN_KEY) missing.push('ADMIN_API_KEY');
if (missing.length) {
    console.error(`Missing env vars: ${missing.join(', ')}`);
    process.exit(1);
}

const s3 = new S3Client({
    region: 'auto',
    endpoint: `https://${R2_ACCOUNT_ID}.r2.cloudflarestorage.com`,
    credentials: {
        accessKeyId: process.env.R2_ACCESS_KEY_ID,
        secretAccessKey: process.env.R2_SECRET_ACCESS_KEY,
    },
});

function slugify(name) {
    return name
        .toLowerCase()
        .replace(/[''´`]/g, '')
        .replace(/[éè]/g, 'e')
        .replace(/&/g, 'and')
        .replace(/[^a-z0-9]+/g, '-')
        .replace(/^-|-$/g, '');
}

function yelpRequest(urlPath) {
    return new Promise((resolve, reject) => {
        const req = https.request({
            hostname: 'api.yelp.com',
            path: urlPath,
            headers: { 'Authorization': `Bearer ${YELP_API_KEY}` }
        }, (res) => {
            const chunks = [];
            res.on('data', c => chunks.push(c));
            res.on('end', () => {
                try {
                    resolve(JSON.parse(Buffer.concat(chunks).toString()));
                } catch (e) { reject(e); }
            });
        });
        req.on('error', reject);
        req.end();
    });
}

async function downloadImage(url) {
    const res = await fetch(url);
    if (!res.ok) throw new Error(`Download failed: ${res.status}`);
    return Buffer.from(await res.arrayBuffer());
}

async function optimizeAndUpload(imageUrl, placeName) {
    const slug = slugify(placeName);
    const key = `places/${slug}.webp`;

    const raw = await downloadImage(imageUrl);
    const optimized = await sharp(raw)
        .resize({ width: 1200, withoutEnlargement: true })
        .webp({ quality: 80 })
        .toBuffer();

    await s3.send(new PutObjectCommand({
        Bucket: R2_BUCKET,
        Key: key,
        Body: optimized,
        ContentType: 'image/webp',
        CacheControl: 'public, max-age=31536000, immutable',
    }));

    const savings = ((1 - optimized.length / raw.length) * 100).toFixed(0);
    const publicUrl = `${R2_PUBLIC_URL}/${key}`;
    console.log(`    R2: ${(raw.length/1024).toFixed(0)}KB → ${(optimized.length/1024).toFixed(0)}KB (-${savings}%) → ${publicUrl}`);
    return publicUrl;
}

async function updatePlacePhoto(placeId, photoUrl) {
    const res = await fetch(`${API_URL}/admin/places/${placeId}`, {
        method: 'PATCH',
        headers: {
            'Content-Type': 'application/json',
            'X-Admin-Key': ADMIN_KEY,
        },
        body: JSON.stringify({ photos: [photoUrl] }),
    });
    if (!res.ok) {
        const err = await res.text();
        throw new Error(`API ${res.status}: ${err}`);
    }
}

async function main() {
    // --- Load candidates ---
    const inputFile = process.argv[2] || `candidates-${new Date().toISOString().split('T')[0]}.json`;
    const filePath = path.resolve(__dirname, inputFile);

    if (!fs.existsSync(filePath)) {
        console.error(`File not found: ${filePath}`);
        process.exit(1);
    }

    const candidates = JSON.parse(fs.readFileSync(filePath, 'utf8'));
    console.log(`=== Yelp Photo Pipeline for ${candidates.length} candidates ===\n`);

    // --- First, get place IDs from the API (we need them for PATCH) ---
    console.log('Fetching place IDs from API...');
    const idsRes = await fetch(`${API_URL}/admin/places?status=in_review&limit=200`, {
        headers: { 'X-Admin-Key': ADMIN_KEY },
    });
    const idsData = await idsRes.json();
    const dbPlaces = idsData.places || [];
    console.log(`Found ${dbPlaces.length} in_review places in DB\n`);

    // Build lookup by name (case-insensitive)
    const dbLookup = {};
    dbPlaces.forEach(p => { dbLookup[p.name.toLowerCase()] = p; });

    // --- Process each candidate ---
    const stats = { found: 0, notFound: 0, uploaded: 0, updated: 0, errors: 0 };

    for (let i = 0; i < candidates.length; i++) {
        const candidate = candidates[i];
        const neighborhood = candidate.neighborhood || 'Miami';
        process.stdout.write(`[${i + 1}/${candidates.length}] ${candidate.name}`);

        // Step 1: Search Yelp
        const term = encodeURIComponent(candidate.name);
        const location = encodeURIComponent(`${neighborhood}, Miami, FL`);

        try {
            const search = await yelpRequest(`/v3/businesses/search?term=${term}&location=${location}&limit=1`);

            if (!search.businesses || search.businesses.length === 0) {
                console.log(' → NOT FOUND on Yelp');
                stats.notFound++;
                await new Promise(r => setTimeout(r, 300));
                continue;
            }

            const biz = search.businesses[0];
            const yelpImageUrl = biz.image_url;

            if (!yelpImageUrl) {
                console.log(` → ${biz.name} (no photo)`);
                stats.notFound++;
                await new Promise(r => setTimeout(r, 300));
                continue;
            }

            console.log(` → ${biz.name}`);
            stats.found++;

            // Step 2: Download, optimize, upload to R2
            const r2Url = await optimizeAndUpload(yelpImageUrl, candidate.name);
            stats.uploaded++;

            // Step 3: Update in DB via API
            const dbPlace = dbLookup[candidate.name.toLowerCase()];
            if (dbPlace) {
                await updatePlacePhoto(dbPlace.id, r2Url);
                console.log(`    DB updated ✓`);
                stats.updated++;
            } else {
                console.log(`    WARN: No DB match for "${candidate.name}"`);
            }

        } catch (err) {
            console.log(` → ERROR: ${err.message}`);
            stats.errors++;
        }

        // Rate limit (Yelp: 500/day)
        await new Promise(r => setTimeout(r, 350));
    }

    console.log('\n=== Summary ===');
    console.log(`Yelp found:    ${stats.found}/${candidates.length}`);
    console.log(`Not found:     ${stats.notFound}`);
    console.log(`Uploaded to R2: ${stats.uploaded}`);
    console.log(`DB updated:    ${stats.updated}`);
    console.log(`Errors:        ${stats.errors}`);
}

main().catch(err => { console.error('Fatal:', err); process.exit(1); });
