# Fotos → R2 (rehost server-side)

Rehost de fotos de places a Cloudflare R2: en **ingesta** (create / bulk / import-from-urls /
PATCH con `photos`) y por **backfill** (`POST /admin/places/backfill-photos`). Objetivo de
seguridad innegociable: **ninguna URL con `key=` (API key de Google Places) sale jamás por la
API B2C** — `PlaceDto`/`ResolvedPlaceDto` y `AdminPlaceDto` filtran con `PhotoUrls.Sanitize`,
y con R2 configurado la ingesta descarta (no persiste) cualquier URL de Google cuyo rehost falle.

---

## ⚠️ RUNBOOK DE DEPLOY — ORDEN BLOQUEANTE (C2)

El filtro `key=` a nivel DTO entra en vigor **al desplegar**. Hoy casi todo `Place.Photos`
en prod son URLs de Google con `key=` → desplegar esta feature **antes** de migrar las fotos
deja la app B2C **sin imágenes** (cada place seguiría respondiendo, pero con `photos` vacío).

Orden obligatorio, sin excepciones:

1. **Configurar credenciales R2 en Railway**: `R2__AccountId`, `R2__AccessKeyId`,
   `R2__SecretAccessKey` (opcionales: `R2__Bucket`, `R2__PublicUrl`). Verificar con
   `POST /admin/places/backfill-photos?dryRun=true` → `r2Configured: true`.
2. **Ejecutar el backfill en bucle** hasta **`converged: true`** (NO basta `remainingPlaces: 0`):
   `POST /admin/places/backfill-photos` (default `limit=20`, cabe bajo el proxy de 40s).
   - **Señal de convergencia HONESTA (G1)**: `converged=true` sólo cuando `!aborted &&
     remainingPlaces==0 && failedPlaces==0 && deferredPlaces==0 && unmigratedPlaces==0`.
     **Relanzar mientras `converged==false`** — equivalentemente, mientras `failedPlaces>0` O
     `deferredPlaces>0` O `unmigratedPlaces>0`, esperando a que expire el backoff (o forzando con
     `retryDeferred=true`). ⚠️ NO uses `remainingPlaces==0` como criterio de parada por sí solo:
     (a) un place que falla **transitoriamente dentro del run** (p. ej. un 503 de Google) se
     difiere y NO cuenta como "no procesado", así que `remainingPlaces` puede ser 0 con un place
     migrable aún sin migrar; (b) un place cuyo **UPLOAD a R2 falla por debajo del umbral de
     aborto** (streak < 3) NO se difiere pero tampoco migra — cuenta como `unmigratedPlaces`.
     Desplegar en cualquiera de esos casos dejaría el place en blanco (su URL de Google se sanea).
     `converged`, `deferredPlaces` (recalculado al FINAL del run, no un snapshot del inicio) y
     `unmigratedPlaces` sí lo reflejan.
   - El bucle **converge garantizado**: los places cuyo rehost falla por la fuente entran en
     backoff (deferral) y no bloquean a los de detrás. Al terminar, revisar `deferredPlaces`:
     si > 0, inspeccionar logs, corregir la causa (un 503 transitorio basta con reintentarlo;
     un fallo permanente puede requerir añadir host a `R2__AllowedPhotoSourceHosts`) y relanzar
     con `retryDeferred=true`.
   - `aborted: true` (`abortReason: r2_upload_unavailable`) = R2 no acepta uploads; el barrido
     se corta para no facturar Google sin progreso. Arreglar R2 y relanzar.
   - Places sin fotos con `GooglePlaceId` (`missingPhotoPlaces`): recuperarlos con
     `recoverMissing=true` (re-obtiene photo refs vía Places Details — factura Details).
3. **Solo entonces** desplegar la rama a Railway. La respuesta debe reportar `converged: true`
   y el censo por dominio (`census`) debe mostrar todas las fotos en `r2.dev` antes del deploy.

**¿Por qué no hay "modo de gracia" en código?** Se valoró degradar por-place mientras queden
fotos sin migrar. El filtro ya degrada por-URL (un place con foto de Google renderiza sin esa
foto, el resto de la app funciona) — el problema pre-backfill es solo de **volumen**, no de
mecánica. Cualquier suavizado (servir la URL con key "temporalmente") rompería el objetivo de
seguridad, que debe cumplirse incondicionalmente. Por eso el gate es operativo, no de código.

---

## Diseño

### Lock del backfill (C1)
`BackfillPhotosCoordinator` (singleton) serializa los barridos con `SemaphoreSlim(1,1)`:
un segundo `POST /admin/places/backfill-photos` concurrente recibe **409
`backfill_already_running`** sin encolarse (encolar duplicaría descargas de Google = doble
facturación, y escrituras R2). `dryRun=true` es read-only y no compite por el lock.
**In-process**: válido con la única réplica de Railway; con 2+ réplicas haría falta lock
distribuido (advisory lock de Postgres) — ver "Scaling invariants" en CLAUDE.md.

### Convergencia del backfill (M1)
Candidatos ordenados por `(CreatedAt, Id)`. Un place que falla **por la fuente** (404/403 de
Google, host bloqueado, imagen corrupta) entra en backoff exponencial (5 min × 2ⁿ, cap 6h) y
se excluye de los siguientes barridos (`deferredPlaces`) → la página avanza y
`remainingPlaces` llega a 0 aunque existan fuentes rotas permanentes. `retryDeferred=true`
ignora el backoff. El estado es in-process: un restart lo limpia y el primer barrido
re-intenta los fallidos una vez (vuelven al backoff — converge igual).

### Señal de convergencia HONESTA (G1)
`remainingPlaces` cuenta candidatos **elegibles no procesados** (por límite/abort); un place
que falla **transitoriamente dentro del run** se procesa (`processedPlaces++`) y luego se
difiere, así que NO cuenta como remaining → `remainingPlaces` podía ser 0 con un place
migrable sin migrar. Doble arreglo para que sea imposible leer "convergido" en falso:
- **`deferredPlaces` se recalcula al FINAL** del run (`candidates.Count(IsDeferred(now))`),
  no un snapshot del inicio — incluye los que fallaron por fuente en ESTE run (acaban de
  entrar en backoff con `DeferredUntil = now + backoff > now`).
- **`unmigratedPlaces`** cuenta los places PROCESADOS que siguen con foto de Google por una
  causa que NO los difiere ni es fallo de fuente: un **fallo de UPLOAD a R2 sub-umbral**
  (streak < 3, o intercalado con OKs que resetean el streak), un abort de R2 a mitad del place,
  o un conflicto de escritura. NO entran en backoff (el siguiente run los retoma de inmediato)
  pero **mantienen `converged=false`** — sin esto, un fallo de upload por debajo del umbral de
  aborto reportaba `remaining=0 && failed=0 && deferred=0` con el place aún en Google (ronda 3).
- **`converged`** = `!aborted && remainingPlaces==0 && failedPlaces==0 && deferredPlaces==0 &&
  unmigratedPlaces==0`. Es la única señal de parada del bucle del runbook. Un place
  permanentemente roto mantiene `converged=false` hasta que el operador lo inspecciona y acepta
  (esa foto renderiza en blanco, que es lo mismo que haría el filtro `key=`), pero el operador
  lo hace **con conocimiento**, no por una señal falsa.

### Circuit breaker de INGESTA (G2) — por INTENTOS y por TIEMPO
El breaker de uploads (M3) vivía solo en el backfill; la **ingesta** (create / bulk /
import-from-urls / PATCH) rehosteaba inline N places × ≤3 fotos sin presupuesto agregado. Con
R2 colgado (acepta TCP, no responde), el timeout de 10s del cliente S3 (M4) acota UN upload
pero un import de muchos places gasta ~10-20s/foto hasta agotar el proxy de Railway (40s); en
ese instante el ct de la request se cancela y el `OperationCanceledException` SÍ propaga → la
request revienta y, con el commit por chunks (M5), lo ya rehosteado se pierde mientras Google
ya se facturó. `IngestPhotoBreaker` (instancia por-request, compartida por todos los places de
un bulk/import) corta el sangrado con **doble presupuesto**:

- **Por intentos**: tras **3 fallos consecutivos de upload** deja de intentar rehost en el
  resto de la request.
- **Por wall-clock (ronda 3)**: `IngestRehostBudget` (default **25s**, configurable vía
  `R2__IngestRehostBudget`) es el presupuesto AGREGADO de la request y una **fracción del
  deadline del proxy** (40s). Acotar solo INTENTOS no basta: contra un R2 colgado ~2 uploads
  (~40s) agotan el proxy **antes** del 3er fallo; el `OperationCanceledException` propagaba y
  tumbaba la creación. `IngestPhotoBreaker.IsOpen` pasa a true también cuando el presupuesto se
  agota, y `IngestPhotoBreaker.Remaining` acota **cada upload individual** (linked CTS +
  `CancelAfter`) para que un solo cuelgue no lo rebase. Al cortar por tiempo queda margen para
  commitear el place antes de que el proxy cancele el ct. `RehostForIngestAsync` además captura
  el `OperationCanceledException` del presupuesto (cuando el ct del caller NO está cancelado) y
  degrada a "sin foto" — **nunca propaga**; una cancelación REAL del caller (ct cancelado) sí
  propaga (el import por chunks decide qué conserva).

En ambos casos el resto de places persiste **sin foto** (URLs con `key=` descartadas) o **con
su URL original** (sin key, para que el backfill las migre luego). Un fallo/cuelgue de upload en
ingesta SIEMPRE degrada a "sin foto"; jamás tumba el place ni aborta la request. (En backfill el
mismo evento **aborta** el barrido; en ingesta **degrada y continúa** — políticas distintas para
el mismo síntoma.)

### Lost updates (M2)
`Place` usa la columna de sistema **`xmin` de Postgres como concurrency token** (sin columna
extra; la migración `UseXminConcurrencyOnPlaces` es no-op en SQL — Npgsql no emite DDL para
columnas de sistema). Escritor stale → `DbUpdateConcurrencyException`:
- **Backfill**: scope de tracking corto (carga el place justo antes, guarda justo después) y
  ante conflicto hace *reload + merge*: re-aplica el mapa `urlOriginal→urlR2` solo sobre las
  URLs que sigan presentes. Lo que escribió el PATCH nunca se pisa (`conflictPlaces` lo cuenta).
- **PATCH/review/postpone admin**: responden **409 `concurrent_update`** — el admin recarga.
- **Resto de escritores de `Place` (g3)**: `DELETE /admin/places/:id`, `reindex-embeddings`,
  `backfill-descriptions`, `translate-batch`, `backfill-opening-hours` también enrutan su
  `SaveChanges` por `TrySaveChangesAsync` → **409 `concurrent_update`** en vez de 500 seco si
  el `backfill-photos` en bucle (u otro writer) tocó la fila entre su read y su save. Los
  endpoints con commit por chunks conservan lo ya persistido antes del 409.

### Circuit breaker de R2 (M3)
`PhotoRehostResult.FailureStage` distingue `Download`/`Decode`/`Blocked` (culpa de la fuente)
de `Upload` (culpa de R2). La descarga de Google se factura ANTES del upload: tras **3 fallos
consecutivos de upload** el barrido aborta (`aborted`/`abortReason`) — no se sigue pagando a
Google sin poder persistir. Los places afectados NO entran en backoff (el siguiente run los
retoma cuando R2 vuelva).

### Timeouts del cliente S3 (M4)
`AmazonS3Config` con `Timeout=10s`, `MaxErrorRetry=1` (el default del SDK es 100s ×
reintentos). Un timeout interno (S3 o HTTP) aflora como `OperationCanceledException` sin que
el ct del caller esté cancelado: `PhotoRehostService` lo degrada a `Failed` — **un R2 colgado
nunca aborta la creación de un place** (la foto con key se descarta; la sin key conserva la
original para el backfill). La cancelación real del caller sí propaga.

### Import masivo (M5)
`PlaceImportService.InsertWithDedupAsync` commitea **por chunks de 10**: si el proxy corta un
import de 500 places a los 40s, lo ya rehosteado queda persistido (nada de Google facturado +
objetos R2 huérfanos sin fila). El re-run dedupa por `GooglePlaceId` / Name+City.

### Recuperación vía GooglePlaceId (M6)
Ingesta fallida → `Photos = null`; el backfill normal no ve esos places. Con
`recoverMissing=true` re-obtiene los photo refs vía `GetDetailsAsync(GooglePlaceId)` y los
rehostea (solo persiste URLs de R2 — jamás las de Details, que llevan key). Los que Google
no puede recuperar entran en el mismo backoff (no se re-factura Details cada barrido).

**Objeto R2 borrado** (decisión documentada): las URLs `r2.dev` persistidas se tratan como
inmutables/permanentes — el backfill no verifica su existencia (un HEAD por foto por barrido
sería coste recurrente para un caso excepcional). Si un objeto desaparece del bucket:
`PATCH /admin/places/:id` con `photos: []` (deja `Photos=null`) y después
`backfill-photos?recoverMissing=true`, o re-pegar URLs fuente en el PATCH. La app tolera
imágenes 404 como cualquier hotlink roto.

### SSRF / hardening de descarga (m2, m3)
- **Allowlist de fuentes** (`R2Options.AllowedPhotoSourceHosts`, ampliable por env):
  `places.googleapis.com`, `*.googleusercontent.com`, `*.ggpht.com`, `wanderlog.com`,
  `*.wanderlog.com`. Todo lo demás se bloquea **antes de tocar la red** (stage `Blocked`).
  Https estricto, sin IP literals (bloquea IMDS/rangos privados de raíz), sin
  localhost/*.internal. El backfill reporta hosts no migrados en `otherDomains`.
- **`AllowAutoRedirect=false`** en el HttpClient del rehost (una fuente allowlisted no puede
  redirigir el GET a un host interno); cualquier 3xx/no-2xx es fallo.
- **Decompression bomb**: content-type debe ser `image/*` (u octet-stream) si se declara;
  `Content-Length` y bytes reales capados a 20 MB; dimensiones validadas con `Image.Identify`
  (solo header, máx 8192px por lado) ANTES de `Image.Load`; `MaxFrames=1`.

### Presupuesto de decodificación (g4 — residual, aceptado y documentado)
El cap de 8192px/lado acota el bitmap decodificado a 8192²×4 ≈ **256 MB por imagen aceptada**.
NO se baja a 4096px (64 MB) a propósito: Google Places sirve fotos de hasta ~4800px y
`googleusercontent` (fotos de usuario) aún mayores — un cap de 4096 rechazaría fuentes
**legítimas**, no solo bombs. ImageSharp 3.x no expone un límite de píxeles en `DecoderOptions`,
así que el check de header (`Image.Identify`) ES la defensa. La presión real está además
acotada por `MaxDownloadBytes=20 MB` (una bomb de 8192px comprimida rara vez cabe) y por el
downscale a 1200px inmediato. Si bajo alta concurrencia de rehosts inline la memoria fuera
problema, la palanca es un `MemoryAllocator` con presupuesto en `Configuration` (no bajar el
cap y romper fuentes válidas).

### Object keys (m5 — colisión por query)
`places/{slug}-{hash8}.webp`, hash sobre scheme+host+**path** (query excluida a propósito:
la rotación de la key de Google no debe cambiar el key — idempotencia del backfill). Riesgo
teórico: un CDN que identifique la imagen solo en la query colapsaría al mismo objeto.
**Aceptado y acotado por la allowlist**: en las fuentes permitidas (Google Places,
googleusercontent, wanderlog) la identidad de la imagen vive en el path. Si se añade una
fuente query-based a la allowlist, incluir entonces un discriminador de query en
`BuildObjectKey`.

### La key y el panel admin (m1)
`AdminPlaceDto` también sanea `Photos` — el panel no necesita la key para MOSTRAR fotos
(renderizarlas facturaría el SKU Place Photos en cada carga y expondría la key en su tráfico).
El único flujo que la necesita es el **import**: las URLs con key viajan en
`GooglePlacePreview` (respuesta de `google-search`) como input efímero del rehost server-side,
nunca desde places persistidos.

## Tests
- `Tests/Unit/PhotoRehostServiceTests.cs` — pipeline, allowlist SSRF, límites de decode,
  degradación de timeouts.
- `Tests/Features/AdminPlacesPhotosTests.cs` — ingesta, backfill, censo, garantía DTO.
- `Tests/Features/AdminPlacesPhotosHardeningTests.cs` — repros permanentes de los pases
  adversariales (lock C1, livelock M1, lost-update M2, breaker M3, R2 colgado M4, chunks M5,
  recovery M6, m1/m2/m4; ronda 2: convergencia honesta **G1**, circuit breaker de ingesta
  **G2** con el knob de latencia de `FakeR2ObjectStore`, writers stale → 409 **g3**).
