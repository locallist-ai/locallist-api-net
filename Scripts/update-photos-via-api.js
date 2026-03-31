const fs = require('fs');
const path = require('path');

const API_URL = 'https://locallist-api-net-production.up.railway.app';
const ADMIN_KEY = process.env.ADMIN_API_KEY;

if (!ADMIN_KEY) {
  console.error('ERROR: ADMIN_API_KEY env var not set');
  process.exit(1);
}

async function main() {
  const photos = JSON.parse(fs.readFileSync(path.join(__dirname, 'r2-photo-urls.json'), 'utf8'));
  const placesData = JSON.parse(fs.readFileSync(path.join(__dirname, 'places-with-ids.json'), 'utf8'));
  const places = placesData.places;

  console.log(`Matching ${photos.length} photos to ${places.length} places...\n`);

  let updated = 0, failed = 0, skipped = 0;

  for (const photo of photos) {
    const place = places.find(p => p.name === photo.name);
    if (!place) {
      console.log(`SKIP: "${photo.name}" — no matching place in DB`);
      skipped++;
      continue;
    }

    try {
      const res = await fetch(`${API_URL}/admin/places/${place.id}`, {
        method: 'PATCH',
        headers: {
          'Content-Type': 'application/json',
          'X-Admin-Key': ADMIN_KEY,
        },
        body: JSON.stringify({ photos: [photo.url] }),
      });

      if (res.ok) {
        console.log(`✓ ${photo.name}`);
        updated++;
      } else {
        const err = await res.text();
        console.log(`✗ ${photo.name} — ${res.status}: ${err}`);
        failed++;
      }
    } catch (err) {
      console.log(`✗ ${photo.name} — ${err.message}`);
      failed++;
    }
  }

  console.log(`\nDone: ${updated} updated, ${failed} failed, ${skipped} skipped`);
}

main().catch(console.error);
