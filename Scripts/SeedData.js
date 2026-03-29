// Data Ingestion Script for LocalList
// Fetches places from Google Places API, filters by quality, analyzes with Gemini AI,
// and outputs a JSON file for human review before importing via the admin bulk endpoint.
//
// Usage:
//   node SeedData.js
//
// Required env vars (in .env file):
//   GEMINI_API_KEY          - Google Gemini API key
//   GOOGLE_MAPS_API_KEY     - Google Maps / Places API key
//     (fallback: EXPO_PUBLIC_GOOGLE_MAPS_IOS_API_KEY)

const { GoogleGenerativeAI } = require('@google/generative-ai');
const fs = require('fs');
const path = require('path');
require('dotenv').config({ path: path.resolve(__dirname, '../../LocalList.API/.env') });

// --- Initialize Gemini AI ---
const gemini = new GoogleGenerativeAI(process.env.GEMINI_API_KEY).getGenerativeModel({
    model: 'gemini-2.5-flash',
    systemInstruction:
        'You are a curator for "LocalList", a travel curation app with the motto "Only The Best. Nothing Else." for modern travelers (Local Explorers). Given a place, evaluate if it deserves to be in our app, classify it, and write a brief "Why This Place" justification. Reject tourist traps and mediocre chain restaurants.',
});

const GOOGLE_API_KEY = process.env.GOOGLE_MAPS_API_KEY || process.env.EXPO_PUBLIC_GOOGLE_MAPS_IOS_API_KEY;

// --- Constants & Settings ---
const TARGET_CITY = 'Miami';
const PLACE_TYPES = ['restaurant', 'cafe', 'bar', 'tourist_attraction', 'park', 'museum'];
const MIN_RATING = 4.3;       // Quality Gate Level 1
const MIN_REVIEWS = 150;      // Quality Gate Level 1
const BATCH_SIZE = 50;
const MAX_PAGES_PER_TYPE = 3; // Google returns max 20 per page, 3 pages = 60 results
const PAGE_TOKEN_DELAY_MS = 2000; // Required delay between paginated requests

// Anti-chain filter: well-known chains that should never appear
const CHAIN_NAMES = [
    "McDonald's", 'Starbucks', 'Subway', 'Burger King', "Wendy's",
    "Dunkin'", 'Taco Bell', "Chili's", "Applebee's", 'IHOP',
    "Denny's", 'Olive Garden', 'Cheesecake Factory',
];

// --- Stats tracking ---
const stats = {
    byCategory: {},
    totalFetched: 0,
    passedQualityGate: 0,
    aiApproved: 0,
    aiRejected: 0,
    aiErrors: 0,
};

/**
 * Fetch places from Google Places Nearby Search API with pagination.
 * Returns up to 60 results per type (3 pages of 20).
 */
async function fetchFromGoogle(type) {
    console.log(`\n--- Searching [${type}] in ${TARGET_CITY} via Google Places API...`);

    const lat = 25.7617;
    const lng = -80.1918;
    const radius = 15000; // 15km

    let allResults = [];
    let nextPageToken = null;

    for (let page = 0; page < MAX_PAGES_PER_TYPE; page++) {
        let url = `https://maps.googleapis.com/maps/api/place/nearbysearch/json?location=${lat},${lng}&radius=${radius}&type=${type}&key=${GOOGLE_API_KEY}`;

        if (nextPageToken) {
            url += `&pagetoken=${nextPageToken}`;
        }

        const response = await fetch(url);
        const data = await response.json();

        if (data.status !== 'OK' && data.status !== 'ZERO_RESULTS') {
            console.warn(`  [Google API Error] ${data.status} - ${data.error_message || ''}`);
            break;
        }

        if (data.results) {
            allResults = allResults.concat(data.results);
        }

        console.log(`  Page ${page + 1}: ${data.results?.length || 0} results`);

        // Check if there's another page
        nextPageToken = data.next_page_token || null;
        if (!nextPageToken) break;

        // Google requires a short delay before the next_page_token becomes valid
        await new Promise(r => setTimeout(r, PAGE_TOKEN_DELAY_MS));
    }

    stats.totalFetched += allResults.length;

    // Quality Gate Level 1: rating, reviews, anti-chain
    const filtered = allResults.filter(p => {
        if (!p.rating || !p.user_ratings_total) return false;
        if (p.rating < MIN_RATING) return false;
        if (p.user_ratings_total < MIN_REVIEWS) return false;
        if (CHAIN_NAMES.some(chain => p.name.includes(chain))) return false;
        return true;
    });

    stats.passedQualityGate += filtered.length;
    console.log(`  [Quality Gate 1] Fetched ${allResults.length}, passed filter: ${filtered.length}`);
    return filtered;
}

/**
 * Analyze a place with Gemini AI (Quality Gate Level 2: vibe check + metadata).
 * Returns structured AI evaluation.
 */
async function analyzeWithGemini(place, type) {
    const prompt = `Analyze this place in ${TARGET_CITY}:
Name: ${place.name}
Google Type: ${type}
Rating: ${place.rating} (${place.user_ratings_total} reviews)
Address: ${place.vicinity}

1. Is this authentic, high-quality, and worthy of LocalList? (Yes/No)
2. Category (exactly one of: Food, Nightlife, Coffee, Outdoors, Wellness, Culture)
3. Best For (array of tags, e.g. ["Date Night", "Groups", "Solo"])
4. Best Time (e.g. "Evening", "Morning", "Weekend brunch", "Late night")
5. Price Range (one of: $, $$, $$$, $$$$)
6. Write a 'whyThisPlace' of 1-2 sentences selling the vibe as if recommending to a friend (trustworthy, direct, local tone, in English).

Respond STRICTLY in this JSON format (no markdown, no extra text):
{"approved": true, "category": "Food", "bestFor": ["Date Night", "Groups"], "bestTime": "Evening", "priceRange": "$$", "whyThisPlace": "A sentence here.", "rejectionReason": null}

If rejected, set approved to false and provide rejectionReason.`;

    try {
        const result = await gemini.generateContent(prompt);
        const text = result.response.text();
        // Extract JSON block from response
        const jsonStr = text.substring(text.indexOf('{'), text.lastIndexOf('}') + 1);
        const parsed = JSON.parse(jsonStr);

        // Validate AI output to mitigate prompt injection
        const validCategories = ['Food', 'Nightlife', 'Coffee', 'Outdoors', 'Wellness', 'Culture'];
        const validPriceRanges = ['$', '$$', '$$$', '$$$$'];

        if (parsed.approved) {
            if (!validCategories.includes(parsed.category)) {
                console.warn(`  [Validation] Invalid category "${parsed.category}" for ${place.name}, defaulting to Food`);
                parsed.category = 'Food';
            }
            if (!validPriceRanges.includes(parsed.priceRange)) {
                parsed.priceRange = '$$';
            }
            if (typeof parsed.whyThisPlace !== 'string' || parsed.whyThisPlace.length > 500) {
                parsed.whyThisPlace = parsed.whyThisPlace?.substring(0, 500) || 'A great spot.';
            }
        }

        // Ensure bestFor is always an array
        if (parsed.bestFor && !Array.isArray(parsed.bestFor)) {
            parsed.bestFor = [String(parsed.bestFor)];
        }

        return parsed;
    } catch (error) {
        console.error(`  [Gemini Error] analyzing ${place.name}: ${error.message}`);
        stats.aiErrors++;
        return { approved: false, rejectionReason: 'Failed AI parsing' };
    }
}

/**
 * Build a candidate object matching the backend's CreatePlaceRequest schema.
 */
function buildCandidate(place, aiData) {
    const addressParts = (place.vicinity || '').split(',');
    const neighborhood = addressParts.length > 1
        ? addressParts[addressParts.length - 1].trim()
        : 'Miami';

    // Store photo references for later processing by process-candidate-photos.js
    // NEVER embed API keys in output files — only store the reference ID
    const photoReference = place.photos?.[0]?.photo_reference || null;

    return {
        name: place.name,
        category: aiData.category || 'Food',
        whyThisPlace: aiData.whyThisPlace || 'A great spot.',
        neighborhood: neighborhood,
        city: TARGET_CITY,
        latitude: place.geometry?.location?.lat || null,
        longitude: place.geometry?.location?.lng || null,
        bestFor: Array.isArray(aiData.bestFor) ? aiData.bestFor : ['Everyone'],
        bestTime: aiData.bestTime || null,
        priceRange: aiData.priceRange || '$$',
        googlePlaceId: place.place_id || null,
        googleRating: place.rating || null,
        googleReviewCount: place.user_ratings_total || null,
        source: 'google_places_gemini',
        status: 'in_review',
        photos: [],
        // Temporary field consumed by process-candidate-photos.js, stripped before import
        googlePhotoReference: photoReference,
    };
}

/**
 * Main pipeline: fetch -> filter -> AI analyze -> collect -> write JSON.
 */
async function run() {
    console.log('=== LocalList Data Ingestion Pipeline ===');
    console.log(`Target: ${TARGET_CITY} | Quality gates: rating >= ${MIN_RATING}, reviews >= ${MIN_REVIEWS}`);
    console.log(`Max batch: ${BATCH_SIZE} candidates\n`);

    if (!GOOGLE_API_KEY) {
        console.error('ERROR: No Google Maps API key found. Set GOOGLE_MAPS_API_KEY or EXPO_PUBLIC_GOOGLE_MAPS_IOS_API_KEY in .env');
        process.exit(1);
    }
    if (!process.env.GEMINI_API_KEY) {
        console.error('ERROR: No Gemini API key found. Set GEMINI_API_KEY in .env');
        process.exit(1);
    }

    const candidates = [];

    for (const type of PLACE_TYPES) {
        if (candidates.length >= BATCH_SIZE) break;

        const places = await fetchFromGoogle(type);

        for (const place of places) {
            if (candidates.length >= BATCH_SIZE) break;

            console.log(`  [AI] Analyzing: ${place.name}`);
            const aiData = await analyzeWithGemini(place, type);

            if (aiData.approved) {
                const candidate = buildCandidate(place, aiData);
                candidates.push(candidate);

                // Track per-category stats
                const cat = candidate.category;
                stats.byCategory[cat] = (stats.byCategory[cat] || 0) + 1;
                stats.aiApproved++;

                console.log(`    APPROVED -> ${candidate.category} | ${candidate.whyThisPlace.substring(0, 60)}...`);
            } else {
                stats.aiRejected++;
                console.log(`    REJECTED -> ${aiData.rejectionReason || 'No reason given'}`);
            }

            // Rate limiter: avoid hitting API quotas
            await new Promise(r => setTimeout(r, 1500));
        }
    }

    // --- Write output JSON ---
    const dateStr = new Date().toISOString().split('T')[0]; // YYYY-MM-DD
    const outputFile = path.resolve(__dirname, `candidates-${dateStr}.json`);
    fs.writeFileSync(outputFile, JSON.stringify(candidates, null, 2), 'utf-8');

    // --- Summary stats ---
    console.log('\n=== Ingestion Summary ===');
    console.log(`Total fetched from Google:    ${stats.totalFetched}`);
    console.log(`Passed Quality Gate 1:        ${stats.passedQualityGate}`);
    console.log(`AI Approved (candidates):     ${stats.aiApproved}`);
    console.log(`AI Rejected:                  ${stats.aiRejected}`);
    console.log(`AI Errors:                    ${stats.aiErrors}`);
    console.log('');
    console.log('Candidates per category:');
    for (const [cat, count] of Object.entries(stats.byCategory).sort((a, b) => b[1] - a[1])) {
        console.log(`  ${cat}: ${count}`);
    }
    console.log('');
    console.log(`Output written to: ${outputFile}`);
    console.log(`\nNext step: review the JSON, then import with:`);
    console.log(`  ./import-candidates.sh ${outputFile} <API_URL> <API_KEY_OR_JWT>`);
}

run().catch(err => {
    console.error('Fatal error:', err);
    process.exit(1);
});
