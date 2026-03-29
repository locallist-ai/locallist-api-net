const fs = require('fs');
const path = require('path');
const sharp = require('sharp');
const { S3Client, PutObjectCommand } = require('@aws-sdk/client-s3');

// Config — credentials and identifiers from env vars
const R2_ACCOUNT_ID = process.env.R2_ACCOUNT_ID;
const R2_BUCKET = process.env.R2_BUCKET || 'locallist-images';
const R2_PUBLIC_URL = process.env.R2_PUBLIC_URL || 'https://pub-7f09e69b5b644703825b6068a05dee8f.r2.dev';

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

async function downloadImage(url) {
  const res = await fetch(url);
  if (!res.ok) throw new Error(`Failed to download ${url}: ${res.status}`);
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
  if (!R2_ACCOUNT_ID || !process.env.R2_ACCESS_KEY_ID || !process.env.R2_SECRET_ACCESS_KEY) {
    console.error('Missing env vars: R2_ACCOUNT_ID, R2_ACCESS_KEY_ID, R2_SECRET_ACCESS_KEY');
    process.exit(1);
  }

  const data = JSON.parse(fs.readFileSync(path.join(__dirname, 'yelp-photos-result.json'), 'utf8'));
  const matched = data.filter(p => p.matched && p.photos.length > 0);

  console.log(`Processing ${matched.length} places with photos...\n`);

  const results = [];

  for (const place of matched) {
    const slug = slugify(place.name);
    const key = `places/${slug}.webp`;
    const yelpUrl = place.photos[0];

    try {
      process.stdout.write(`${place.name}... `);
      const raw = await downloadImage(yelpUrl);
      const optimized = await optimizeImage(raw);
      const publicUrl = await uploadToR2(key, optimized);
      const savings = ((1 - optimized.length / raw.length) * 100).toFixed(0);
      console.log(`OK (${(raw.length/1024).toFixed(0)}KB → ${(optimized.length/1024).toFixed(0)}KB, -${savings}%) → ${publicUrl}`);
      results.push({ name: place.name, url: publicUrl });
    } catch (err) {
      console.log(`FAILED: ${err.message}`);
    }

    // Rate limit: 100ms between requests
    await new Promise(r => setTimeout(r, 100));
  }

  // Generate SQL for DB update
  const sqlLines = results.map(r => {
    const escaped = r.name.replace(/'/g, "''");
    return `  ('${escaped}', ARRAY['${r.url}'])`;
  });

  const sql = `UPDATE places SET photos = v.photos, updated_at = NOW()
FROM (VALUES
${sqlLines.join(',\n')}
) AS v(name, photos)
WHERE places.name = v.name
  AND places.photos IS NULL;`;

  fs.writeFileSync(path.join(__dirname, 'r2-photo-urls.json'), JSON.stringify(results, null, 2));
  fs.writeFileSync(path.join(__dirname, 'update-photos.sql'), sql);

  console.log(`\n✓ ${results.length}/${matched.length} photos uploaded to R2`);
  console.log(`✓ SQL saved to update-photos.sql`);
  console.log(`✓ URLs saved to r2-photo-urls.json`);
}

main().catch(console.error);
