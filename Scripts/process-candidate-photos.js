// Process photos for candidate places from SeedData.js output
// Downloads Google Places photos, optimizes with Sharp, uploads to Cloudflare R2
//
// Usage: node process-candidate-photos.js <candidates.json>
// Env: R2_ACCESS_KEY_ID, R2_SECRET_ACCESS_KEY, GOOGLE_MAPS_API_KEY
// Output: overwrites the input JSON with R2 CDN URLs in photos[]

const fs = require('fs');
const path = require('path');
const sharp = require('sharp');
const { S3Client, PutObjectCommand } = require('@aws-sdk/client-s3');

// R2 config — credentials and identifiers from env vars
const R2_ACCOUNT_ID = process.env.R2_ACCOUNT_ID;
const R2_BUCKET = process.env.R2_BUCKET || 'locallist-images';
const R2_PUBLIC_URL = process.env.R2_PUBLIC_URL || 'https://pub-7f09e69b5b644703825b6068a05dee8f.r2.dev';

const GOOGLE_API_KEY = process.env.GOOGLE_MAPS_API_KEY || process.env.EXPO_PUBLIC_GOOGLE_MAPS_IOS_API_KEY;

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
        .replace(/['']/g, '')
        .replace(/&/g, 'and')
        .replace(/[^a-z0-9]+/g, '-')
        .replace(/^-|-$/g, '');
}

async function downloadGooglePhoto(photoReference) {
    const url = `https://maps.googleapis.com/maps/api/place/photo?maxwidth=1200&photoreference=${photoReference}&key=${GOOGLE_API_KEY}`;
    const res = await fetch(url, { redirect: 'follow' });
    if (!res.ok) throw new Error(`Google Photo API ${res.status}`);
    return Buffer.from(await res.arrayBuffer());
}

async function optimizeImage(buffer) {
    return sharp(buffer)
        .resize({ width: 1200, withoutEnlargement: true })
        .webp({ quality: 80 })
        .toBuffer();
}

async function uploadToR2(key, buffer) {
    await s3.send(new PutObjectCommand({
        Bucket: R2_BUCKET,
        Key: key,
        Body: buffer,
        ContentType: 'image/webp',
        CacheControl: 'public, max-age=31536000, immutable',
    }));
    return `${R2_PUBLIC_URL}/${key}`;
}

async function main() {
    const inputFile = process.argv[2];
    if (!inputFile) {
        console.error('Usage: node process-candidate-photos.js <candidates.json>');
        process.exit(1);
    }

    if (!GOOGLE_API_KEY) {
        console.error('Missing GOOGLE_MAPS_API_KEY env var');
        process.exit(1);
    }

    if (!process.env.R2_ACCESS_KEY_ID || !process.env.R2_SECRET_ACCESS_KEY) {
        console.error('Missing R2_ACCESS_KEY_ID or R2_SECRET_ACCESS_KEY env vars');
        process.exit(1);
    }

    if (!R2_ACCOUNT_ID) {
        console.error('Missing R2_ACCOUNT_ID env var');
        process.exit(1);
    }

    const filePath = path.resolve(inputFile);
    const candidates = JSON.parse(fs.readFileSync(filePath, 'utf8'));

    // Filter candidates that have a Google photo reference but no R2 photo yet
    const needsPhoto = candidates.filter(
        (c) => c.googlePhotoReference && (!c.photos || c.photos.length === 0)
    );

    console.log(`Found ${needsPhoto.length}/${candidates.length} candidates needing photos\n`);

    let success = 0;
    let failed = 0;

    for (const candidate of needsPhoto) {
        const slug = slugify(candidate.name);
        const key = `places/${slug}.webp`;

        try {
            process.stdout.write(`${candidate.name}... `);

            const raw = await downloadGooglePhoto(candidate.googlePhotoReference);
            const optimized = await optimizeImage(raw);
            const publicUrl = await uploadToR2(key, optimized);

            // Update the candidate in-place
            candidate.photos = [publicUrl];

            const savings = ((1 - optimized.length / raw.length) * 100).toFixed(0);
            console.log(
                `OK (${(raw.length / 1024).toFixed(0)}KB -> ${(optimized.length / 1024).toFixed(0)}KB, -${savings}%)`
            );
            success++;
        } catch (err) {
            console.log(`FAILED: ${err.message}`);
            failed++;
        }

        // Rate limit: 200ms between requests
        await new Promise((r) => setTimeout(r, 200));
    }

    // Remove temporary googlePhotoReference field before writing output
    for (const candidate of candidates) {
        delete candidate.googlePhotoReference;
    }

    // Write updated candidates back
    const outputFile = filePath.replace('.json', '-with-photos.json');
    fs.writeFileSync(outputFile, JSON.stringify(candidates, null, 2));

    console.log(`\nDone: ${success} uploaded, ${failed} failed`);
    console.log(`Output: ${outputFile}`);
}

main().catch(console.error);
