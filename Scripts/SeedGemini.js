// Gemini-powered candidate generation for LocalList
// Uses Gemini 2.5 Flash to research and generate place candidates for Miami
// without requiring Google Maps API.
//
// Usage:
//   node SeedGemini.js
//
// Required env vars:
//   GEMINI_API_KEY - Google Gemini API key

const { GoogleGenerativeAI } = require('@google/generative-ai');
const fs = require('fs');
const path = require('path');

// Load .env if available (env vars from system take precedence)
const envPaths = [path.resolve(__dirname, '.env'), path.resolve(__dirname, '../.env')];
for (const p of envPaths) {
    if (fs.existsSync(p)) { require('dotenv').config({ path: p }); break; }
}

if (!process.env.GEMINI_API_KEY) {
    console.error('ERROR: GEMINI_API_KEY not set');
    process.exit(1);
}

const gemini = new GoogleGenerativeAI(process.env.GEMINI_API_KEY).getGenerativeModel({
    model: 'gemini-2.5-flash',
    generationConfig: { temperature: 0.7 },
});

// --- Existing places to exclude ---
const existingPlaces = [
    "1-800-Lucky", "Bayshore Park", "Beau Monde Pilates", "Bill Baggs Cape Florida State Park",
    "Brothers Davie Pro Rodeo", "Cantina La Veinte", "Casa Bake", "Casadonna", "Crandon Park",
    "Dante's HiFi", "Dogs & Cats Walkway", "El Bagel", "Everglades National Park",
    "Fairchild Tropical Botanic Garden", "Garcia's Seafood Grille & Fish Market", "Havana Harry's",
    "Katana Japanese Restaurant", "Lagniappe", "Lung Yai Thai Tapas", "Macchialina", "Matsuri",
    "NutriDrip at Fontainebleau", "Oleta River State Park", "Panther Coffee", "Peel Soft Serve",
    "Pinecrest Gardens", "Ritz-Carlton Spa, Key Biscayne", "Robert Is Here Fruit Stand",
    "Sky Coffee Buenos Aires", "South Pointe Park", "Superblue Miami",
    "Swizzle Rum Bar & Drinkery", "Tea Room (EAST Miami)", "The Bass Museum of Art", "The Underline",
    "Tremble Fitness", "True Loaf Bakery", "Unarthodox Miami", "Vizcaya Museum & Gardens"
];

// --- How many we need per category ---
const targets = {
    Food: 16,
    Nightlife: 11,
    Coffee: 9,
    Culture: 9,
    Wellness: 11,
    Outdoors: 5,
};

const DELAY_MS = 3000; // delay between Gemini calls to respect rate limits

/**
 * Ask Gemini to generate candidates for one category.
 */
async function generateForCategory(category, count) {
    const existingList = existingPlaces.join(', ');

    const prompt = `You are a curator for "LocalList", a travel curation app for Miami with the motto "Only The Best. Nothing Else."

Your task: recommend exactly ${count} real, currently-open places in Miami for the "${category}" category.

STRICT RULES:
- Every place MUST be a real, currently operating business/location in Miami-Dade County
- NO chains (McDonald's, Starbucks, Chili's, etc.)
- NO places that have permanently closed
- Rating should be 4.3+ with significant reviews
- Focus on places locals love, not just tourist traps
- Diverse neighborhoods: Wynwood, Brickell, Little Havana, Coconut Grove, Design District, South Beach, Coral Gables, Little Haiti, Edgewater, Midtown, Downtown, Key Biscayne, etc.

EXCLUDE these places (already in our database):
${existingList}

Category guidance:
${getCategoryGuidance(category)}

Return ONLY a JSON array (no markdown, no explanation) with exactly ${count} objects, each with:
{
  "name": "Exact business name",
  "category": "${category}",
  "whyThisPlace": "1-2 sentences selling the vibe, as a local friend recommending it. Trustworthy, direct tone, English.",
  "neighborhood": "Miami neighborhood name",
  "city": "Miami",
  "latitude": 25.xxxx,
  "longitude": -80.xxxx,
  "bestFor": ["tag1", "tag2"],
  "bestTime": "Morning|Lunch|Afternoon|Evening|Late night|Weekend brunch|All day",
  "priceRange": "$|$$|$$$|$$$$",
  "googleRating": 4.x,
  "googleReviewCount": approximate_number,
  "source": "gemini_research",
  "status": "in_review"
}

IMPORTANT: Coordinates must be real and accurate for Miami. Do your best to provide the actual lat/lng.`;

    console.log(`\n--- Generating ${count} candidates for [${category}]...`);

    try {
        const result = await gemini.generateContent(prompt);
        const text = result.response.text();

        // Extract JSON array from response
        const start = text.indexOf('[');
        const end = text.lastIndexOf(']') + 1;
        if (start === -1 || end === 0) {
            console.error(`  [ERROR] No JSON array found for ${category}`);
            return [];
        }

        const candidates = JSON.parse(text.substring(start, end));

        // Validate and clean each candidate
        const validCategories = ['Food', 'Nightlife', 'Coffee', 'Outdoors', 'Wellness', 'Culture'];
        const validPriceRanges = ['$', '$$', '$$$', '$$$$'];

        const cleaned = candidates.map(c => ({
            name: String(c.name || '').trim(),
            category: validCategories.includes(c.category) ? c.category : category,
            whyThisPlace: String(c.whyThisPlace || 'A great spot.').substring(0, 500),
            neighborhood: String(c.neighborhood || 'Miami').trim(),
            city: 'Miami',
            latitude: typeof c.latitude === 'number' ? c.latitude : null,
            longitude: typeof c.longitude === 'number' ? c.longitude : null,
            bestFor: Array.isArray(c.bestFor) ? c.bestFor.map(String) : ['Everyone'],
            bestTime: c.bestTime || null,
            priceRange: validPriceRanges.includes(c.priceRange) ? c.priceRange : '$$',
            googlePlaceId: null,
            googleRating: typeof c.googleRating === 'number' ? c.googleRating : null,
            googleReviewCount: typeof c.googleReviewCount === 'number' ? c.googleReviewCount : null,
            source: 'gemini_research',
            status: 'in_review',
            photos: [],
        }));

        // Filter out any that match existing places
        const existingLower = existingPlaces.map(n => n.toLowerCase());
        const filtered = cleaned.filter(c => !existingLower.includes(c.name.toLowerCase()));

        console.log(`  Generated: ${candidates.length}, after dedup: ${filtered.length}`);
        filtered.forEach(c => console.log(`    ${c.name} (${c.neighborhood}) - ${c.priceRange}`));

        return filtered;
    } catch (error) {
        console.error(`  [ERROR] ${category}: ${error.message}`);
        return [];
    }
}

function getCategoryGuidance(category) {
    const guidance = {
        Food: `Restaurants, from casual to fine dining. Include variety: Cuban, Japanese, Italian, seafood, farm-to-table, tacos, brunch spots. Mix of $$ and $$$ price points. Focus on places with character and a story.`,
        Nightlife: `Bars, lounges, live music venues, rooftop bars, speakeasies, dance clubs. Not just South Beach clubs — include Wynwood, Brickell, Little Havana, Coconut Grove spots. Diverse vibes: chill cocktail bars, latin dance, jazz, rooftop.`,
        Coffee: `Specialty coffee shops, bakery-cafes, tea houses. Focus on third-wave coffee, unique atmosphere, good for working or hanging out. Not just espresso — places with personality.`,
        Culture: `Museums, galleries, street art, historic sites, markets, performing arts venues, unique cultural experiences. Miami's art scene, Art Deco, Latin culture, Haitian art, etc.`,
        Wellness: `Yoga studios, spas, fitness studios, meditation, outdoor wellness, wellness cafes. Not big-box gyms — boutique, unique experiences.`,
        Outdoors: `Parks, beaches, kayaking, nature trails, gardens, waterfront spots. Hidden gems, not just the obvious beaches. Include spots good for sunrise, sunset, or active recreation.`,
    };
    return guidance[category] || '';
}

async function run() {
    console.log('=== LocalList Gemini Research Pipeline ===');
    console.log(`Target: Miami | Using Gemini 2.5 Flash for research`);
    console.log(`Existing places: ${existingPlaces.length}`);
    console.log(`Target new candidates: ${Object.values(targets).reduce((a, b) => a + b, 0)}\n`);

    const allCandidates = [];

    for (const [category, count] of Object.entries(targets)) {
        const candidates = await generateForCategory(category, count);
        allCandidates.push(...candidates);

        // Rate limit between categories
        await new Promise(r => setTimeout(r, DELAY_MS));
    }

    // --- Write output ---
    const dateStr = new Date().toISOString().split('T')[0];
    const outputFile = path.resolve(__dirname, `candidates-${dateStr}.json`);
    fs.writeFileSync(outputFile, JSON.stringify(allCandidates, null, 2), 'utf-8');

    // --- Summary ---
    const catCounts = {};
    allCandidates.forEach(c => { catCounts[c.category] = (catCounts[c.category] || 0) + 1; });

    console.log('\n=== Generation Summary ===');
    console.log(`Total candidates generated: ${allCandidates.length}`);
    console.log('By category:');
    for (const [cat, count] of Object.entries(catCounts).sort((a, b) => b[1] - a[1])) {
        console.log(`  ${cat}: ${count}`);
    }
    console.log(`\nOutput: ${outputFile}`);
    console.log(`\nNext steps:`);
    console.log(`  1. Review the JSON for accuracy`);
    console.log(`  2. Import: ./import-candidates.sh ${outputFile} <API_URL> <API_KEY>`);
    console.log(`  3. Curate in Admin UI (swipe approve/reject)`);
}

run().catch(err => {
    console.error('Fatal error:', err);
    process.exit(1);
});
