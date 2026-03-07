// Data Ingestion Script for LocalList
// Usage: node SeedData.js

const { neon } = require('@neondatabase/serverless');
const { GoogleGenerativeAI } = require('@google/generative-ai');
const crypto = require('crypto');
require('dotenv').config({ path: '../../LocalList.API/.env' });

// 1. Initialize Clients
const sql = neon(process.env.DATABASE_URL);
const gemini = new GoogleGenerativeAI(process.env.GEMINI_API_KEY).getGenerativeModel({
    model: 'gemini-2.5-flash',
    systemInstruction:
        'Eres un curador de "LocalList", una app de viajes estilo "Only The Best. Nothing Else." para viajeros modernos (Local Explorers). Dado un lugar, evalúa si merece estar en nuestra app, clasifícalo y escribe un breve "Why This Place" justificativo. Rechaza trampas para turistas o restaurantes mediocres de cadena.',
});
const GOOGLE_API_KEY = process.env.EXPO_PUBLIC_GOOGLE_MAPS_IOS_API_KEY || process.env.GOOGLE_MAPS_API_KEY;

// 2. Constants & Settings
const TARGET_CITY = 'Miami';
const PLACE_TYPES = ['restaurant', 'cafe', 'bar', 'tourist_attraction', 'park', 'museum'];
const MIN_RATING = 4.3; // Quality Gate Level 1
const MIN_REVIEWS = 150; // Quality Gate Level 1
const BATCH_SIZE = 50;

async function fetchFromGoogle(type) {
    console.log(`\n🔍 Buscando [${type}] locales en ${TARGET_CITY} vía Google...`);

    // Miami Coordinates (approx)
    const lat = 25.7617;
    const lng = -80.1918;
    const radius = 15000; // 15km

    const url = `https://maps.googleapis.com/maps/api/place/nearbysearch/json?location=${lat},${lng}&radius=${radius}&type=${type}&key=${GOOGLE_API_KEY}`;

    const response = await fetch(url);
    const data = await response.json();

    if (data.status !== 'OK') {
        console.warn(`[Google API Error] ${data.status} - ${data.error_message || ''}`);
        return [];
    }

    // Quality Gate Level 1 (Integrity & Baseline Quality)
    const filtered = data.results.filter(p => {
        if (!p.rating || !p.user_ratings_total) return false;
        if (p.rating < MIN_RATING) return false;
        if (p.user_ratings_total < MIN_REVIEWS) return false;
        // Basic anti-chain filter based on name length/common words (simplified for script)
        if (p.name.includes("McDonald's") || p.name.includes('Starbucks') || p.name.includes('Subway')) return false;
        return true;
    });

    console.log(`✅ [Quality Gate 1] Encontrados ${data.results.length}, pasaron el fitro: ${filtered.length}`);
    return filtered;
}

async function analyzeWithGemini(place, type) {
    // Quality Gate Level 2 (Vibe Check & Metadata Generation)
    const prompt = `Analiza este lugar en ${TARGET_CITY}:\nNombre: ${place.name}\nTipo Google: ${type}\nRating: ${place.rating} (${place.user_ratings_total} reviews)\nDirección: ${place.vicinity}\n\n1. ¿Es auténtico, de alta calidad y merece estar en LocalList? (Sí/No)\n2. Categoría (Food, Nightlife, Coffee, Outdoors, Wellness, Culture): \n3. Best For (ej: Date Night, Groups, Solo): \n4. Rango de Precio ($ al $$$$): \n5. Escribe un 'whyThisPlace' de 1-2 frases vendiendo la vibra del lugar como si se lo recomendaras a un amigo (tono confiable, directo, local, en inglés).\n\nResponde estrictamente en este formato JSON: {"approved": boolean, "category": "String", "bestFor": "String", "priceRange": "String", "whyThisPlace": "String", "rejectionReason": "String"}`;

    try {
        const result = await gemini.generateContent(prompt);
        const text = result.response.text();
        // Parse JSON block
        const jsonStr = text.substring(text.indexOf('{'), text.lastIndexOf('}') + 1);
        return JSON.parse(jsonStr);
    } catch (error) {
        console.error(`❌ [Gemini Error] al analizar ${place.name}:`, error.message);
        return { approved: false, rejectionReason: "Failed AI parsing" };
    }
}

async function insertToNeon(place, aiData, type) {
    // Insert with 'in_review' status for the Admin ERP
    const id = crypto.randomUUID();
    const addressParts = (place.vicinity || '').split(',');
    const neighborhood = addressParts.length > 1 ? addressParts[addressParts.length - 1].trim() : 'Miami';

    // Map Google Photo reference (placeholder logic for MVP, usually you build the full URL later)
    const photos = place.photos ? [`https://maps.googleapis.com/maps/api/place/photo?maxwidth=800&photoreference=${place.photos[0].photo_reference}&key=API_KEY`] : [];

    try {
        await sql`
      INSERT INTO places (
        id, name, category, city, neighborhood, "coordsLat", "coordsLng", 
        "whyThisPlace", "bestFor", "priceRange", photos, source, status, 
        "created_at", "updated_at", "rejectionReason"
      ) VALUES (
        ${id}, ${place.name}, ${aiData.category || 'Food'}, ${TARGET_CITY}, ${neighborhood}, 
        ${place.geometry?.location?.lat || 0}, ${place.geometry?.location?.lng || 0},
        ${aiData.whyThisPlace || 'A great spot.'}, ${aiData.bestFor || 'Everyone'}, 
        ${aiData.priceRange || '$$'}, ${JSON.stringify(photos)}, 'Google Places / Gemini API', 
        'in_review', NOW(), NOW(), ${aiData.rejectionReason || null}
      )
    `;
        console.log(`💾 [Guardado] ${place.name} -> Listo para revisión humana`);
        return true;
    } catch (err) {
        if (err.message.includes('unique constraint')) {
            console.log(`⏭️ [Skip] ${place.name} ya existe en BD.`);
        } else {
            console.error(`❌ [DB Error] Guardando ${place.name}: ${err.message}`);
        }
        return false;
    }
}

async function run() {
    console.log("🚀 Iniciando LocalList Data Ingestion Pipeline (Modo: in_review)");
    let totalSaved = 0;

    for (const type of PLACE_TYPES) {
        if (totalSaved >= BATCH_SIZE) break;

        const places = await fetchFromGoogle(type);

        for (const place of places) {
            if (totalSaved >= BATCH_SIZE) break;

            console.log(`\n🤖 Consultando AI sobre: ${place.name}`);
            const aiData = await analyzeWithGemini(place, type);

            if (aiData.approved) {
                console.log(`⭐ [AI Aprobado] ${place.name} - ${aiData.whyThisPlace}`);
                const success = await insertToNeon(place, aiData, type);
                if (success) totalSaved++;
            } else {
                console.log(`🛑 [AI Rechazado] ${place.name} - Motivo: ${aiData.rejectionReason}`);
                // Optionally save rejected places for analytics, but skipping for now to save DB space
            }

            // Rate link breaker
            await new Promise(r => setTimeout(r, 1500));
        }
    }

    console.log(`\n🎉 Ingestion finalizada. Guardados ${totalSaved} lugares nuevos 'in_review'.`);
    process.exit(0);
}

run();
