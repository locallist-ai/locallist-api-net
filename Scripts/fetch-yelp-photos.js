#!/usr/bin/env node
/**
 * Fetches photos from Yelp for all curated Miami places.
 * Usage: YELP_API_KEY=xxx node fetch-yelp-photos.js
 * Output: yelp-photos-result.json
 */

const https = require('https');
const fs = require('fs');
const path = require('path');

const YELP_API_KEY = process.env.YELP_API_KEY;
if (!YELP_API_KEY) {
  console.error('Missing YELP_API_KEY env var');
  process.exit(1);
}

const places = require('./miami_curated_import.json');

function yelpRequest(path) {
  return new Promise((resolve, reject) => {
    const req = https.request({
      hostname: 'api.yelp.com',
      path,
      headers: { 'Authorization': `Bearer ${YELP_API_KEY}` }
    }, (res) => {
      const chunks = [];
      res.on('data', c => chunks.push(c));
      res.on('end', () => {
        try {
          resolve(JSON.parse(Buffer.concat(chunks).toString()));
        } catch (e) {
          reject(e);
        }
      });
    });
    req.on('error', reject);
    req.end();
  });
}

async function fetchPhotosForPlace(place) {
  const term = encodeURIComponent(place.name);
  const location = encodeURIComponent(`${place.neighborhood}, Miami, FL`);

  // Search only (1 call per place, saves quota)
  const search = await yelpRequest(`/v3/businesses/search?term=${term}&location=${location}&limit=1`);

  if (!search.businesses || search.businesses.length === 0) {
    console.log(`  NOT FOUND: ${place.name}`);
    return { name: place.name, photos: [], matched: false };
  }

  const biz = search.businesses[0];
  const photos = biz.image_url ? [biz.image_url] : [];

  console.log(`  OK: ${place.name} → ${biz.name} (${photos.length} photo)`);

  return {
    name: place.name,
    yelpName: biz.name,
    yelpId: biz.id,
    photos,
    matched: true
  };
}

async function main() {
  console.log(`Fetching Yelp photos for ${places.length} places...\n`);

  const results = [];
  let found = 0;
  let notFound = 0;

  for (let i = 0; i < places.length; i++) {
    const place = places[i];
    process.stdout.write(`[${i + 1}/${places.length}]`);

    try {
      const result = await fetchPhotosForPlace(place);
      results.push(result);
      if (result.matched) found++;
      else notFound++;
    } catch (err) {
      console.log(`  ERROR: ${place.name} — ${err.message}`);
      results.push({ name: place.name, photos: [], matched: false, error: err.message });
      notFound++;
    }

    // Rate limit: Yelp allows 500/day, ~5/sec. Be conservative.
    if (i < places.length - 1) {
      await new Promise(r => setTimeout(r, 300));
    }
  }

  const outPath = path.join(__dirname, 'yelp-photos-result.json');
  fs.writeFileSync(outPath, JSON.stringify(results, null, 2));

  console.log(`\nDone! ${found} found, ${notFound} not found.`);
  console.log(`Results saved to ${outPath}`);
}

main().catch(err => { console.error(err); process.exit(1); });
