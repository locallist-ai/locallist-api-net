#!/usr/bin/env node
/**
 * A/B de CALIDAD de extracción de slots entre gemini-2.5-flash y un modelo candidato
 * (p. ej. la gama 3.x Flash-Lite, más barata). NO puede ejecutarse sin una API key
 * real; por eso vive como script standalone y no como test.
 *
 * Uso:
 *   # 1) Confirmar el id exacto del candidato (lista los modelos con "flash" en el nombre):
 *   GEMINI_API_KEY=xxx node gemini-model-ab.js --list
 *
 *   # 2) Correr el A/B contra el id confirmado:
 *   GEMINI_API_KEY=xxx node gemini-model-ab.js <candidate-model-id>
 *   # ej: GEMINI_API_KEY=xxx node gemini-model-ab.js gemini-flash-lite-latest
 *
 * Replica EXACTAMENTE la generationConfig del fix (thinkingBudget=0, maxOutputTokens 1024,
 * responseMimeType application/json, temperature 0.2) para que la comparación sea justa.
 *
 * Criterio de aceptación del cambio de modelo (juicio manual sobre el output):
 *   - city / days / groupType / categories / budget correctos en los 6 casos (ES + EN).
 *   - aiMessage coherente y en el idioma del usuario.
 *   - sin slots alucinados (no rellenar lo que el usuario no dijo).
 *   - finishReason=STOP (sin truncación) y coste menor.
 * Cambiar el id en appsettings (Llm:Providers[0].Model) SOLO si la calidad se mantiene.
 */

const https = require('https');

const API_KEY = process.env.GEMINI_API_KEY;
if (!API_KEY) {
  console.error('Falta GEMINI_API_KEY env var.');
  process.exit(1);
}

const BASELINE = 'gemini-2.5-flash';

// Casos de prueba: texto libre que debe extraer los 5 slots críticos. ES + EN.
const CASES = [
  { lang: 'es', msg: 'Voy a Miami 3 dias con mi pareja, nos gusta la gastronomia y la playa, presupuesto medio.' },
  { lang: 'es', msg: 'Finde romantico en Sevilla, 2 dias, cultura y tapas, sin gastar mucho.' },
  { lang: 'es', msg: 'Viaje en familia con ninos a Barcelona una semana, parques y comida, gama alta.' },
  { lang: 'en', msg: 'Solo trip to Miami for 4 days, into nightlife and art, mid budget.' },
  { lang: 'en', msg: 'Weekend with friends in Lisbon, 2 days, food and viewpoints, cheap.' },
  { lang: 'en', msg: 'Couple getaway to Rome, 5 days, history and fine dining, splurge.' },
];

const SCHEMA_HINT = `Extract trip details as JSON (the word json is required). Fill ONLY slots the user mentioned; never invent.
Schema: {"extracted":{"city":string|null,"days":number|null,"groupType":"solo|couple|friends|family|family-kids"|null,"categories":string[],"budget":"budget|moderate|premium"|null},"aiMessage":"short reply in the user's language, no place names"}`;

function buildPrompt(userMsg) {
  return `You are a focused travel planning assistant for LocalList. Your ONLY purpose is extracting trip details.\n${SCHEMA_HINT}\n<user_input>\n${userMsg}\n</user_input>`;
}

function post(model, body) {
  const data = JSON.stringify(body);
  const opts = {
    hostname: 'generativelanguage.googleapis.com',
    path: `/v1beta/models/${model}:generateContent`,
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      'x-goog-api-key': API_KEY,
      'Content-Length': Buffer.byteLength(data),
    },
  };
  return new Promise((resolve, reject) => {
    const req = https.request(opts, (res) => {
      let chunks = '';
      res.on('data', (c) => (chunks += c));
      res.on('end', () => resolve({ status: res.statusCode, body: chunks }));
    });
    req.on('error', reject);
    req.write(data);
    req.end();
  });
}

function listModels() {
  return new Promise((resolve, reject) => {
    https
      .get(
        { hostname: 'generativelanguage.googleapis.com', path: '/v1beta/models', headers: { 'x-goog-api-key': API_KEY } },
        (res) => {
          let chunks = '';
          res.on('data', (c) => (chunks += c));
          res.on('end', () => resolve(chunks));
        }
      )
      .on('error', reject);
  });
}

async function run(model, userMsg) {
  const body = {
    contents: [{ parts: [{ text: buildPrompt(userMsg) }] }],
    generationConfig: {
      temperature: 0.2,
      maxOutputTokens: 1024,
      responseMimeType: 'application/json',
      thinkingConfig: { thinkingBudget: 0 },
    },
  };
  const { status, body: raw } = await post(model, body);
  if (status !== 200) return { ok: false, status, raw: raw.slice(0, 200) };
  const doc = JSON.parse(raw);
  const cand = doc.candidates && doc.candidates[0];
  const finish = cand && cand.finishReason;
  const text = cand && cand.content && cand.content.parts && cand.content.parts[0] && cand.content.parts[0].text;
  const usage = doc.usageMetadata || {};
  return {
    ok: true,
    finish,
    text,
    inTok: usage.promptTokenCount,
    outTok: usage.candidatesTokenCount,
    thinkTok: usage.thoughtsTokenCount,
  };
}

(async () => {
  if (process.argv.includes('--list')) {
    const raw = await listModels();
    const doc = JSON.parse(raw);
    console.log('Modelos con "flash" en el nombre (confirma aqui el id exacto del Flash-Lite):');
    for (const m of doc.models || []) {
      if (/flash/i.test(m.name)) console.log(`  ${m.name}  —  ${m.displayName || ''}`);
    }
    return;
  }

  const candidate = process.argv[2];
  if (!candidate) {
    console.error('Uso: node gemini-model-ab.js <candidate-model-id>  (o --list para confirmar el id)');
    process.exit(1);
  }

  for (const c of CASES) {
    console.log(`\n=== [${c.lang}] ${c.msg}`);
    for (const model of [BASELINE, candidate]) {
      const r = await run(model, c.msg);
      if (!r.ok) {
        console.log(`  ${model}: HTTP ${r.status} ${r.raw}`);
        continue;
      }
      console.log(
        `  ${model}: finish=${r.finish} tok(in/out/think)=${r.inTok}/${r.outTok}/${r.thinkTok ?? 0}`
      );
      console.log(`     ${(r.text || '(empty)').replace(/\s+/g, ' ').trim()}`);
    }
  }
  console.log('\nJuicio manual: compara slots extraidos (city/days/groupType/categories/budget),');
  console.log('coherencia del aiMessage, idioma, alucinaciones, finishReason y tokens.');
})();
