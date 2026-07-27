# LocalList.API.NET

Parent context: see `../CLAUDE.md` for brand, domain concepts, and conventions.

When the user says "backend", "api", "net", ".net", or "c#", they mean this active project (`LocalList.API.NET`).

| | Details |
|---|---|
| **Tech** | .NET 10 (Controllers), C#, Entity Framework Core, Railway PostgreSQL |
| **Architecture** | Vertical Slice Architecture (VSA) — feature folders |
| **Deploy** | Railway (Dockerfile) |
| **Auth** | Dual-scheme JWT multi-issuer: `AppScheme` HS256 (app B2C, issuer `locallist-api`) + `FirebaseScheme` RS256 JWKS (admin interno). El scheme se selecciona por el `iss` del token en `Shared/Startup/AuthenticationExtensions.cs` (policy scheme `Multi`). |
| **AI** | Cadena de extracción (chat slot-filling + builder preferences) en `gemini-3.1-flash-lite` (primer provider de `Llm:Providers`). Builder pipeline en `Features/Builder/Services/`. Chat slot-filling en `Features/Chat/Services/`. Traducciones/descripciones/embeddings siguen su path Gemini propio (fuera de la cadena). |
| **Rate Limit** | 100 req/min global. Endpoints medidos (sliding window, techo por IP encadenado anti account-farming + refinamiento por identidad, bucket alto SOLO AppScheme): **builder/chat-generate** (desde F4 exigen `[Authorize]`: el bucket anon solo acota spam pre-401, nunca llega a Gemini) techo 60/hr por IP (`Builder__RateLimitPerHourPerIp`) + 5/hr anon · 20/hr auth (`Builder__RateLimitPerHour` / `__RateLimitPerHourAuthenticated`); **chat/turn** techo 120/hr por IP (`Chat__RateLimitTurnsPerHourPerIp`) + 20/hr anon · 40/hr auth (`Chat__RateLimitTurnsPerHourAnonymous` / `__Authenticated`). Auth 10/15min. Waitlist 5/60s. CityRequest 5/60s por IP (`CityRequestLimit`). Admin 60/min. Photos 60/min por IP (`PhotoLimit`, `GooglePlaces__PhotoRateLimitPerMinute`). SharedPlan 60/min por IP (`SharedPlanLimit`, `SharedPlan__RateLimitPerMinute`; sliding window; cubre el `GET /plans/shared/:token` anónimo de S1: acota scraping/enumeración de tokens). Import 20/hr por IP (`ImportLimit`, `Import__RateLimitPerHourPerIp`; sliding window; cubre `POST /import/video` — cara por request, techo anti-farming sobre la cuota por usuario — Y `POST /import/plan` — acepta placeIds published arbitrarios sin exigir import previo, sin este techo solo lo acotaría el global). `UseRateLimiter` va después de `UseAuthentication`. |

## Running Locally

```bash
cd locallist-api-net  # desde el raíz del monorepo
dotnet restore
dotnet run
```

Required User Secrets / Environment Variables:

**Core**
- `ConnectionStrings__DefaultConnection` — Postgres URL (Railway privada; nunca exponer públicamente)
- `FIREBASE_PROJECT_ID`
- `JWT_SECRET` — HS256 signing key para tokens de la app (≥32 bytes). También legible como `Jwt__Secret` via config binding.

**Gemini (Builder + RAG embeddings)**
- `Gemini__ApiKey`
- `Gemini__EmbeddingModel` — `gemini-embedding-001` (768 dims, L2-norm). **No** `text-embedding-004` (retirado 2026-01-14). Se usa en `EmbeddingService` para RAG.

**LLM fallback chain (camino crítico: chat slot-filling + builder preferences)**
- Cadena ordenada en `appsettings.json` → `Llm:Providers` (gemini → openai → mistral → anthropic). Abstracción en `Shared/AI/Llm/` (`ILlmClient`, `FallbackLlmClient`, circuit breaker `LlmProviderHealthRegistry`: 3 fallos seguidos → skip 60s).
- `OpenAI__ApiKey` — opcional. Activa GPT-5.4 Nano como backup.
- `Mistral__ApiKey` — opcional. Activa Mistral Small como backup.
- `Anthropic__ApiKey` — opcional. Activa Claude Haiku 4.5 como backup (último por coste).
- Un provider sin key se omite de la cadena (log en boot). Solo con `Gemini__ApiKey` el comportamiento es el clásico. `chat_turns.ai_provider/model` registran quién respondió realmente.
- Traducciones, descripciones y embeddings siguen solo-Gemini (fuera de la cadena).
- `GeminiLlmClient` envía `thinkingConfig.thinkingBudget=0` (slot/preference extraction no razonan; con thinking ON los thinking-tokens truncaban el JSON contra `maxOutputTokens` → `finishReason=MAX_TOKENS` → `invalid_json`) y aplica un suelo `minOutputTokens=1024` (espejo de `reasoning_effort:minimal`+floor del cliente OpenAI). `MAX_TOKENS` se reporta como `truncated`, no `invalid_json`. El id del modelo vive en `Llm:Providers` (campo `Model`); el cliente es agnóstico.
- Error usuario vs diagnóstico admin: si la cadena falla de verdad (no un "no te he entendido" legítimo), `/chat/turn` devuelve un mensaje genérico (`ChatStrings.AiUnavailable`) + flag `error:"ai_unavailable"`, sin exponer provider/status. El motivo real (body no-2xx truncado ~500 + redactado con PiiRedactor: cuota/429, etc.) va a `chat_turns.error_message` y es visible solo-admin vía `GET /admin/analytics/chat-turns` (`ErrorMessage` en el DTO). La API key nunca se loguea ni persiste (vive en headers, no en el body).

**Coverage gate (ciudades en vivo)**
- `Coverage__LiveCities` — allowlist explícita de ciudades expuestas (default `["Miami"]`). Soporta índices (`Coverage__LiveCities__0=Miami`) o escalar separado por comas (`Coverage__LiveCities=Miami,Sevilla`). NO se deriva de "la ciudad tiene places" — hay ciudades de TEST con places que no deben exponerse. Helper central `ICityCoverageService` (impl en `Features/Cities/CityCoverageService.cs`, normaliza con `CityNameNormalizer`). Consumido por `GET /cities/live` (selector de la app), `/chat/turn` (bloquea ciudad no cubierta con `cityUnsupported:true`) y `/chat/generate` (defensa: 400 `city_unsupported` estructurado, no 404 seco).

**Google Places (admin ingestion)**
- `GooglePlaces__ApiKey` — Google Places API (New) key. Activa en GCP: API "Places API (New)". Si no está, `POST /admin/places/google-search` devuelve 404 graceful.
- `GooglePlaces__PhotoApiKey` — opcional. Key SEPARADA para el proxy de fotos (`GET /places/:id/photos/:index`). Si falta, cae en fallback a `GooglePlaces__ApiKey`; si NINGUNA está, el endpoint degrada a 404. `GooglePlaces__PhotoDailyBudgetCap` (default 10000) = techo diario in-process de llamadas `/media` de pago.
- `Api__PublicBaseUrl`: opcional (default `""`). Base URL pública de esta API (p.ej. la de Railway) usada para sintetizar en `PlaceDto`/`ResolvedPlaceDto.Photos` la URL absoluta del proxy de fotos `GET /places/:id/photos/0`, y también la URL del preview admin `GET /admin/places/photo-preview` (`AdminPlacePhotoPreviewUrls`). Vacía en dev: se sirve una ruta relativa y el caller la resuelve contra su propia base. Ver `Shared/Dtos/PlacePhotoUrls.cs`.

**Routing (Mapbox)**
- `Mapbox__AccessToken` — opcional. Si no está, routing se deshabilita gracefully (stops sin `travelFromPrevious`).

**Analytics (PostHog)**
- `PostHog__ApiKey` — opcional. Eventos `plan_generated`, `user_signed_up`, `user_signed_in`, etc.
- `PostHog__Host` — opcional, por defecto `https://eu.i.posthog.com`.

**Email marketing (Klaviyo / Waitlist)**
- `Klaviyo__ApiKey` — opcional. Sin él, el servicio de email se deshabilita silenciosamente.
- `Klaviyo__WaitlistListId` — ID de lista de Klaviyo para la waitlist.

**Monetización (F4 — RevenueCat / tier)**
- El webhook es un TRIGGER, NO la fuente de verdad. El tier se deriva del estado autoritativo consultado a la REST API de RevenueCat (`GET /subscribers/{app_user_id}`), no del payload — un secreto filtrado no permite forjar grants ni congelar pro con `event_timestamp_ms` falso.
- Anti god-token: se resuelve el `User` primero y se verifica contra RC SOLO sus ids propios (`User.Id` / `RcCustomerId` enlazado), nunca un `app_user_id` arbitrario del payload — así el payload no puede desacoplar "a quién verifico" de "a quién acredito". El webhook NO escribe `rc_customer_id`. Rate-limit por IP `RevenueCatWebhookLimit` (60/min).
- `REVENUECAT_WEBHOOK_AUTH` — **requerido** para `POST /webhooks/revenuecat`. Valor exacto del header `Authorization` configurado en el dashboard de RevenueCat. Verificado antes de deserializar el body (fail-closed 503 si falta). También legible como `RevenueCat__WebhookAuthToken`.
- `REVENUECAT_REST_API_KEY` — **requerido** para conceder tier. Secret API key (sk_...) de RC para verificar el suscriptor. Distinta del secreto del webhook. Sin ella no se concede upgrade (webhook 503, RC reintenta). También `RevenueCat__RestApiKey`.
- `RevenueCat__PlusEntitlementId` — id del entitlement que mapea a tier `pro` (default `plus`).
- Enforcement: catálogo Plus vs free DECIDIDO (2026-07-13) y aplicado server-side. `PlanGenerationGateService` (`Shared/Usage/`) gatea `POST /chat/generate` y `POST /builder/chat` (ambos `[Authorize]` desde F4): 3 planes IA/mes free (contador atómico en `usage_counters`, upsert condicional) · cap antiabuso 50/día Plus (429) · duración ≤3 días free / ≤14 Plus. El hard cap de días vive en `PlanLimits.MaxPlanDurationDays` (`Shared/Constants/`), única fuente de verdad para `PlanGenerationGateService.PlusMaxDays` Y para el `[Range(1,14)]` de TODOS los DTOs con día/duración (edición + admin) — evita el drift 7→14 que dejaba a un Plus generando 14 días pero sin poder editarlos. Ambos endpoints rechazan ciudad no cubierta ANTES del gate (`400 city_unsupported`, sin consumir contador; `ICityCoverageService`). Cuando el clamp de días derivados por el LLM recorta, la respuesta trae `clamped:{field,requested,applied,upsell}`. Tier SIEMPRE fresco de DB. Errores estructurados para el upsell (`plan_limit_reached`, `duration_requires_plus`, `daily_cap_reached`).
- **Cupo de planes guardados (5 free) — en `POST /plans` (PlansController), NO en la generación** (decisión Pablo 2026-07-22): límite de ALMACENAMIENTO independiente del contador mensual (un free con 5 planes manuales sigue generando sus 3 IA/mes). `403 {error:"saved_plans_limit_reached", used, limit:5}`; `DELETE /plans/:id` libera hueco; Plus sin límite.
- `GET /account` expone la cuota mensual proactiva: `aiPlansMonth:{used, limit, resetsAt}` (`limit` omitido = ilimitado para Plus). Los campos `clamped` y `aiPlansMonth` los consume el task app-side — nombres estables, documentados en `Features/Billing/README.md`.
- Detalle y huecos (favoritos sin modelo, multi-ciudad imposible por construcción) en `Features/Billing/README.md`. `[RequirePro]` (`Shared/Auth/`) sigue disponible para gates binarios.

**Video import (F2 — extracción, servicio autocontenido)**
- `Import__ApiKey` — opcional; fallback a `Gemini__ApiKey` (misma cuenta). La clave separada solo existe para aislar cuota/coste del import si conviene.
- `Import__Model` — modelo multimodal, default `gemini-3.1-flash` (**NO** lite: el import es OCR-pesado y flash-lite pierde recall sobre texto pequeño).
- `Import__MaxDurationSeconds` (600), `Import__MaxSizeBytes` (157286400 = 150MB), `Import__AllowedMimeTypes` (mp4/quicktime/webm), `Import__FilePollDelayMs` (1000), `Import__FilePollMaxAttempts` (60).
- `Import__ThirdPartyEnabled` (default **false**) — gating del camino de TERCEROS (T1). `Import__RateLimitPerHourPerIp` (default 20) — techo por IP del endpoint.
- Slice `Features/Import/` (`VideoExtractionService`) + `Shared/AI/GeminiFileClient.cs`: sube el vídeo a la Gemini File API (subida resumable → poll hasta ACTIVE), extrae sitios con generateContent multimodal, **borra el fichero tras extraer** (finally con `CancellationToken.None`: minimiza retención de contenido de terceros — relevante legalmente). NO usa la cadena de fallback `Llm:Providers` (solo Gemini tiene el fichero; si falla → `ExtractionUnavailable`, retry manual). El vídeo es INPUT HOSTIL: el JSON extraído se sanea reutilizando `OutputValidator`/`OutputSanitizer` del slice Chat (cero URLs, categoría contra taxonomía, drift/canary → drop). Diagnóstico persistido en `video_import_metrics` — inventario de retención honesto: SÍ tokens/coste/latencia/metadatos técnicos **y el `city`/`country`/`language` extraídos** (contexto de mercado para decidir cobertura, mismo propósito que `city_requests`); NO bytes/file_uri/transcript/caption, NO nombres de sitios (solo el count), NO identidad del uploader. Retención indefinida como agregado diagnóstico salvo revisión legal. Ratios de tokens de media verificados vs pricing oficial en `VideoCostEstimator` (258 tok/s vídeo + 32 tok/s audio).
- **T1 — endpoint `POST /import/video`** (`ImportController`): feature del catálogo **Plus**. `[Authorize]` AppScheme (anónimo → 401) + gate Plus (tier FRESCO de DB → free = `403 import_requires_plus`). Los metadatos `platform`/`creatorHandle` viajan en la **query string** (no en el form): la query se lee antes del body, así el gating de terceros corre SIEMPRE pre-body — un multipart hostil no puede forzar streamear 150 MB para acabar en 403. v1 = contenido PROPIO (`platform=self`); `platform≠self` con `Import:ThirdPartyEnabled` off → `403 third_party_import_disabled` sin leer el vídeo. Multipart solo-fichero con streaming a temp file (borrado en `finally`); cap 150 MB SOLO aquí vía `[RequestSizeLimit]` (`[DisableFormValueModelBinding]` para que MVC no consuma el body antes que el `MultipartReader`). Rechazo barato ANTES de cuota: no-multipart/boundary o multipart malformado → `400 import_invalid_request`, MIME → `400 import_unsupported_format`, tamaño → `400 import_too_large`, sin part de fichero → `400 import_missing_file`; defensa en profundidad post-metadata autoritativa del File API: duración → `400 import_video_too_long`. Cuota por usuario sobre `usage_counters` (**30/mes + 10/día**, ventanas `import_monthly`/`import_daily`, TOCTOU-safe) → agotada = `429 import_limit_reached` (`window`; 429 y no 403 porque ya es Plus, sin upsell — como el cap diario Plus). **Reembolso ligado a la FACTURACIÓN de Gemini** (`IUsageCounterService.ReleaseAsync`, decrement atómico con suelo): éxito, `no_places_found` y cualquier fallo POST-2xx de generateContent (`ExtractionUnavailableException.Billed`: `truncated`/MAX_TOKENS, `content_filtered_*`, `invalid_json` — el contenido del vídeo puede provocarlos a voluntad y Gemini ya cobró ~150k tokens) MANTIENEN la cuota; fallos SIN facturar (upload/poll, `duration_unknown`, HTTP no-2xx de generate, límites autoritativos VideoTooLong/TooLarge) REEMBOLSAN ambas ventanas. Respuestas: 200 (candidatos saneados, ciudad/idioma/vibes/confianza + `platform`/`creatorHandle` + campos de match de T3; SIN internals de Gemini), `422 no_places_found`, `503 import_unavailable`. NO persiste plan todavía (creación = T4). Capability para la app en `GET /account` (`importThirdPartyEnabled`).
- **T3 — matching contra catálogo** (`ImportMatchingService`): tras la extracción, el endpoint matchea cada candidato contra los places **published** de la ciudad detectada (normalizada con `CityNameNormalizer`) y enriquece el DTO con `matchedPlaceId` (Guid?), `matchedPlaceName` y `matchConfidence` (`high`|`medium`|null) — ADITIVO, no rompe el contrato de T1. Los no matcheados salen igual (la app los pinta como "no está en LocalList"). **NO** crea places (curación = admin) ni llama a Google (coste/ToS). Matching DETERMINISTA (v1, sin trgm de DB): UNA query trae TODOS los places published (proyección id/name/city) y el filtro por ciudad normalizada corre EN MEMORIA — no hay columna de ciudad normalizada, aceptable hoy Miami-only (~100s de filas; import Plus-only + rate-limited); TODO al ir multi-ciudad: `normalized_city` indexada + predicado SQL. Estrategia: normaliza nombres (lower + sin diacríticos + colapsa no-alfanumérico; el ruido genérico —artículos + "restaurant/bar/cafe/coffee"— se quita ANTES de comparar, dejando los tokens "core"). `high` = igualdad normalizada exacta **o** contains SOBRE TOKENS CORE: run contiguo con el lado corto aportando **≥ 2 tokens core** (contains de 1 token PROHIBIDO da igual su longitud: "Havana"/"Grill" no matchean nada; "Café Cubano" no hace contains contra "Cubano"). `medium` = solape bag-of-words ≥ 60% de los tokens core del candidato **y** ≥ 2 tokens en común (un único token NUNCA matchea); medium ignora el orden ("Casa Marina"↔"Marina Casa Club") — aceptado v1, es una SUGERENCIA para la app, no un enlace fuerte. Ranking: mayor tier → (solo medium) mayor solape → menos tokens core → nombre normalizado más corto; en contains el solape NO participa (prefería el nombre más largo). Si ≥2 places DISTINTOS empatan en la misma tupla (cadenas: dos "Starbucks") → **null por AMBIGÜEDAD** (suprimir > enlazar una sucursal arbitraria por Id); sigue siendo determinista. Ciudad detectada ausente o no presente en catálogo → todos unmatched, SIN error. `VideoExtractionResult.MetricId` propaga la fila de diagnóstico para que el endpoint anote `video_import_metrics.num_matched` (un UPDATE por PK, best-effort). El extractor v1 no aporta geo, así que el match es solo por nombre.
- **T4 — crear el plan desde un import confirmado** (`ImportPlanController`, `POST /import/plan`): última pieza backend de F2. La app confirma qué places (subconjunto de los `matchedPlaceId` de T3) quiere y este endpoint materializa el plan. `[Authorize]` AppScheme (anónimo → 401) + gate Plus fresco de DB (free → `403 import_requires_plus`) — MISMO patrón que T1. **SIN cuota nueva** (la cuota 30/mes·10/día mide llamadas a Gemini y aquí no hay ninguna), pero **SÍ comparte el techo por IP `ImportLimit` (20/hr) con T1**: el endpoint acepta placeIds published arbitrarios SIN exigir un import previo, así que sin él solo lo acotaría el global 100/min. Body: `{ city, days(1-14, CLAMP con PlanLimits, no rechaza), placeIds: Guid[], planName?, platform?(self|tiktok|instagram|other), creatorHandle? }`. Gating de terceros idéntico a T1 (`platform≠self` con `Import:ThirdPartyEnabled` off → `403 third_party_import_disabled`). Ciudad no cubierta → `400 city_unsupported` (`ICityCoverageService`, mismo gate que builder/chat). Validación de places ATÓMICA y OPACA: dedup + orden CANÓNICO por Id (el plan es función del SET — el mismo set barajado produce el mismo plan), no vacío, y TODOS deben existir + `published` + de la ciudad (comparada normalizada) — si alguno falla → `400 import_invalid_places` SIN decir cuál y SIN crear nada; TOCTOU con hard-delete admin entre el SELECT y el INSERT (23503 del FK plan_stops→places) → mismo `400 import_invalid_places`, nunca 500 (predicado `IsForeignKeyViolation` compartido con Favorites); cap `MaxStopsPerDay×days` → `400 import_too_many_places`. **Scheduling**: reusa el `SchedulingService` DETERMINISTA (semilla FNV de placeIds+ciudad+días) sobre el SET FIJO (sin RAG/ranking: el usuario ya eligió), pasando los places en el orden canónico (la query de DB no garantiza orden y el scheduler es sensible a él) → reparte en días con `opening_hours` + travel como un plan normal. Diferencia clave vs generación: allí el scheduler sobre-selecciona y descartar es inocuo; aquí cada place es elección explícita, así que un place que el walk-clock descartaría por viabilidad (cerrado/hueco/leg/tope) se **RECONCILIA** como stop SIN horario al final de su día — invariante: el plan contiene SIEMPRE los N placeIds confirmados, ni uno menos. `source="imported"` (NUNCA curated —`isCurated` mira `source=="curated"`— ni showcase), `type="custom"`, `visibility="private"` (default S0), owner = caller. **Atribución de creador**: columnas nuevas en `plans` `imported_from_platform`(16, solo terceros; null para self) + `imported_creator_handle`(64, saneado con regex `^@?[A-Za-z0-9_.\-]{1,63}$`, guardado sin `@`; inválido → null, nunca tumba la creación ni persiste sucio) — expuestas en `PlanDetailDto` como `importedFromPlatform`/`importedCreatorHandle` ADITIVO (JsonIgnore WhenWritingNull: omitidas en planes no-import). NO usa `cloned_from` (eso es plan→plan del share-link). Naming: `planName` saneado (trim + sin control + em/en-dash→`-` + cap 120); ausente/vacío → fallback bilingüe localizado de `PlanNamingService` por el `Accept-Language`. Respuesta `201` con el `PlanDetailDto` (con stops+places) para navegar directo al plan.

**Social (S0 cimiento + S1 share-link)**
- **S0 (cimiento, ya en main)**: `plans.visibility` (`private`|`unlisted`|`public`) = **fuente de verdad** de la autorización; `is_public` es un espejo legacy sincronizado por los setters de la entidad (la app vieja consume `isPublic`, derivado de `visibility=='public'`). `share_token` (único, NULLs múltiples). `IPlanAccessService` centraliza el acceso (owner/editor/viewer/public/blocked; **`unlisted` NO se resuelve por GUID** — solo por token). Planes privados por defecto.
- **S1 (share-link, primer pilar)**: compartir un plan por enlace. `POST /plans/:id/share` (owner) genera el token y sube `private→unlisted`; `DELETE` revoca (`unlisted→private`, `public` sigue public); `GET /plans/shared/:token` resuelve POR TOKEN para anónimos. **División de responsabilidades**: `IPlanAccessService` autoriza por GUID+userId y jamás resuelve `unlisted`; el share-link es un capability aparte (la posesión del `share_token` secreto ES la autorización) y su resolución vive en `PlanShareController.GetShared`, no en el servicio de acceso. Mutar la visibilidad sí exige ownership (el controller consulta `IsOwner`). Universal links = acción externa de Apple pendiente; el backend es agnóstico del dominio del link.
- **Privacidad del DTO anónimo**: `GET /plans/shared/:token` sirve `PlanDetailDto` con `createdById` EXCLUIDO (`with { CreatedById = null }`) — un enlace `unlisted` no revela la identidad interna del dueño a cualquiera que tenga el link. El resto (nombre, ciudad, stops, atribución de import) ya es contenido del plan.
- **4 MINORs de S0 cerrados en S1** (condición para introducir `unlisted`): (a) `GET /plans` filtra por `visibility=='public'`, no por `is_public` (un `unlisted` jamás lista); (b) el read-path del follow (`GET /follow/active`, `PATCH .../next|/skip`) re-chequea `IPlanAccessService.CanView` en cada read → si el plan pasó a private o el owner bloqueó al follower, `403 {error:"plan_access_revoked"}` (pause/complete NO re-chequean: no sirven contenido y un follower siempre puede abandonar su sesión); (c) CHECK `ck_user_blocks_no_self` (anti self-block); (d) CHECK `ck_plans_visibility_domain` (`visibility ∈ {private,unlisted,public}`). c+d en la migración `AddSocialShareConstraints`.

## Project Structure (VSA)

```
LocalList.API.NET/
├── Program.cs                          # Composition root: pipeline + llama a las extensiones de Shared/Startup/
├── Features/
│   ├── Account/
│   │   └── AccountController.cs        # GET /account, DELETE /account
│   ├── Admin/
│   │   ├── Analytics/
│   │   │   ├── AdminChatTurnsController.cs    # GET /admin/analytics/chat-turns, /stats
│   │   │   ├── AdminPlanMetricsController.cs  # GET /admin/analytics/plan-metrics, /stats
│   │   │   └── AdminAnalyticsDtos.cs
│   │   ├── Cities/
│   │   │   └── AdminCitiesController.cs       # DELETE /admin/cities/:id
│   │   ├── Places/
│   │   │   ├── AdminPlacesController.*.cs     # CRUD + backfill + translate + photo-preview (ver Endpoints; partial: .cs ctor, .Reads, .Google, .Crud, .Backfill, .Translation)
│   │   │   ├── GooglePlacesService.cs         # Google Places API (New) integration. NUNCA construye URLs con key: ResolvePhotos sintetiza referencias a AdminPlacePhotoPreviewUrls
│   │   │   ├── AdminPlacePhotoPreviewUrls.cs  # Síntesis de GET /admin/places/photo-preview?googlePlaceId=X&index=I (preview pre-guardado, sin Place.Id aún)
│   │   │   ├── PlaceImportService.cs          # Lógica de ingesta extraída del controller. Google-sourced: Photos siempre null (runtime-only, GooglePlaceId basta)
│   │   │   └── AdminDtos.cs
│   │   ├── Plans/
│   │   │   ├── AdminPlansController.*.cs      # CRUD + translate curated plans (partial: .cs ctor/reads/delete, .Create, .Update, .Translation)
│   │   │   └── AdminPlanDtos.cs
│   │   └── Subcategories/
│   │       ├── AdminSubcategoriesController.cs  # CRUD /admin/subcategories
│   │       └── AdminSubcategoriesDtos.cs
│   ├── Auth/
│   │   ├── AuthController.cs           # POST /auth/sync (Firebase token → user sync, admin)
│   │   ├── AppAuthController.cs        # POST /auth/signin|register|login|refresh (app HS256)
│   │   ├── AuthDtos.cs                 # Sync/Signin/Register/Login/Refresh DTOs
│   │   └── Services/
│   │       ├── JwtTokenService.cs          # HS256 access token issuer
│   │       ├── RefreshTokenService.cs      # SHA-256 refresh rotation (30d lifetime)
│   │       ├── PasswordHasher.cs           # bcrypt para email/password
│   │       ├── GoogleIdTokenValidator.cs   # Valida ID token Google vs JWKS
│   │       ├── AppleIdTokenValidator.cs    # Valida ID token Apple vs JWKS
│   │       └── JwksRetriever.cs            # Caché JWKS para Apple
│   ├── Billing/
│   │   ├── BillingController.cs        # POST /webhooks/revenuecat (anonymous, secreto Authorization verificado pre-body)
│   │   ├── BillingEventProcessor.cs    # Único escritor de User.Tier; deriva tier de RC (no del payload), idempotente
│   │   ├── IRevenueCatClient.cs        # Contrato + status; el webhook es trigger, RC REST es la fuente de verdad
│   │   ├── RevenueCatClient.cs         # GET /subscribers/{app_user_id} con secret API key → entitlement activo?
│   │   ├── RevenueCatDtos.cs           # RevenueCatWebhookRequest/Event (payload NO confiable para el tier)
│   │   └── README.md                   # Doc F4 + modelo de seguridad + PENDIENTE producto: catálogo features Plus
│   ├── Builder/
│   │   ├── BuilderController.cs        # POST /builder/chat
│   │   ├── BuilderDtos.cs              # BuilderChatRequest
│   │   ├── Services/
│   │   │   ├── PreferenceExtractorService.cs   # Gemini → ExtractedPreferences
│   │   │   ├── PlaceRankingService.cs          # Reranking determinista ponderado
│   │   │   ├── PlanGenerationService.cs        # Orquesta RAG + prefs + scheduler
│   │   │   ├── PlanNamingService.cs            # Genera nombre y descripción del plan (helper estático; usado dentro de Builder)
│   │   │   ├── PlanNamingProvider.cs           # IPlanNamingService (Shared) → delega en PlanNamingService, para que Import consuma el naming por interfaz
│   │   │   └── SchedulingService.*.cs          # Scheduler determinista por semilla; implementa ISchedulingService (Shared). (partial: .cs API, .Constants, .Selection, .Ordering, .DayWalk, .Refinements, .Helpers)
│   │   └── Shared/
│   │       └── GroupTypePolicy.cs       # Reglas de capacidad por tipo de grupo
│   ├── Chat/
│   │   ├── ChatController.cs           # POST /chat/turn, /chat/generate, DELETE /chat/session/:id
│   │   ├── ChatDtos.cs
│   │   ├── I18n/
│   │   │   └── ChatStrings.cs
│   │   └── Services/
│   │       ├── ChatAgentService.*.cs        # Orquesta slot-filling + sesión + generación (partial: .cs orquestación ProcessTurnAsync, .Constants, .Responses, .Session, .Slots, .Generation, .Helpers)
│   │       ├── SlotExtractorService.cs     # Gemini → extrae slots de texto libre (sanitizadores IA en Shared/AI/Security/)
│   │       ├── PromptInjectionDetector.cs  # Detecta prompt injection en input
│   │       ├── JailbreakPatternLibrary.cs  # Patrones de jailbreak conocidos
│   │       ├── ResponseDriftDetector.cs    # Detecta drift off-topic en respuestas AI
│   │       ├── SuspicionTracker.cs         # Trackea sesiones sospechosas (rate de fallos)
│   │       └── ChatSecLogger.cs            # Log estructurado de eventos de seguridad
│   ├── Cities/
│   │   ├── CitiesController.cs         # GET /cities/search, GET /cities/live, POST /cities
│   │   ├── CityRequestsController.cs   # POST /cities/request (anonymous; feedback "¿No ves tu ciudad?" → city_requests, texto inerte validado por dominio + dedup 24h)
│   │   ├── CityCoverageService.cs      # ICityCoverageService impl (allowlist Coverage:LiveCities)
│   │   └── CityNameNormalizer.cs       # Unicode FormD normalization para búsqueda
│   ├── Favorites/
│   │   └── FavoritesController.cs      # PUT/DELETE /favorites/:placeId (idempotentes), GET /favorites (paginado), GET /favorites/ids. [Authorize] AppScheme. Cap 50 free / ∞ Plus con tier FRESCO de DB; atomicidad del cap vía pg_advisory_xact_lock por usuario (no hay fila-contador única como usage_counters)
│   ├── Follow/
│   │   ├── FollowController.cs         # POST /follow/start (IDOR #116 cerrado vía IPlanAccessService.CanView), GET /active, PATCH next/skip/pause/complete
│   │   └── FollowDtos.cs              # FollowStartRequest
│   ├── Import/                         # F2 — import de vídeo
│   │   ├── ImportController.cs         # T1 — POST /import/video: [Authorize(App)]+gate Plus (TierGate), multipart streaming a temp file, gating terceros, cuota 30/mes·10/día, mapea el resultado a DTO
│   │   ├── ImportPlanController.cs     # T4 — POST /import/plan: SOLO gates + mapping (Plus vía TierGate, terceros, coverage, days-clamp, cap); delega la materialización en ImportPlanService. [Authorize(App)]
│   │   ├── ImportPlanService.cs        # T4 núcleo (extraído del controller): validación atómica/opaca de places, seed FNV, scheduling (ISchedulingService) + reconcile no-loss (BuildStops internal, testeable sin HTTP), persistencia atómica
│   │   ├── ImportAttribution.cs        # Helpers atribución compartidos T1/T4: NormalizePlatform (default self) + SanitizeCreatorHandle (regex estricta, sin '@') — semántica unificada
│   │   ├── ImportDtos.cs               # ImportVideoResponse/ImportPlaceDto (proyección T1+match T3) + CreateImportPlanRequest (body T4)
│   │   ├── ImportMatchingService.cs    # T3 — matching determinista de candidatos vs catálogo published de la ciudad (1 query, en memoria); high/medium/null
│   │   ├── VideoExtractionService.cs   # T2 — bytes vídeo + caption → JSON estricto de sitios (sube/extrae/borra)
│   │   ├── VideoOutputSanitizer.cs     # Sanea el JSON hostil (reusa OutputValidator/OutputSanitizer de Shared/AI/Security/)
│   │   ├── VideoCostEstimator.cs       # Estimación de tokens de media (258/s vídeo + 32/s audio, verificado)
│   │   ├── VideoExtractionModels.cs    # ExtractedVideoPlace, VideoExtractionResult
│   │   └── VideoExtractionExceptions.cs # VideoTooLong/TooLarge/UnsupportedFormat/NoPlacesFound/ExtractionUnavailable
│   │   # (ImportOptions.cs movido a Shared/AI/ — lo consume GeminiFileClient, no debe vivir en un slice)
│   ├── Places/
│   │   ├── PlacesController.cs         # GET /places, GET /places/:id
│   │   └── Photos/                     # Proxy de fotos de Google (runtime-only, ToS-compliant)
│   │       ├── PlacePhotosController.cs  # GET /places/:id/photos/:index (302 al photoUri, key server-side)
│   │       ├── PlacePhotoService.cs      # Place Details (FieldMask=photos, gratis) + /media (key en header) → photoUri
│   │       ├── PhotoBudgetCounter.cs     # Circuit breaker de presupuesto diario (in-process, reset UTC)
│   │       └── GooglePhotoHostValidator.cs  # Allowlist de host (*.googleusercontent.com) compartida por este proxy y el preview admin de AdminPlacesController
│   ├── Plans/
│   │   ├── PlansController.cs          # GET /plans, GET /plans/:id (autoriza vía IPlanAccessService; anónimo exige visibility='public')
│   │   ├── PlanDtos.cs                 # PlanDto/PlanDetailDto: isPublic derivado de visibility=='public' (back-compat app vieja); PlanDetailDto expone importedFromPlatform/importedCreatorHandle (F2 T4, aditivo, omitido si null)
│   │   ├── PlanEditController.cs       # PUT /plans/:id/stops (CanEdit), DELETE /plans/:id (IsOwner) — ambos vía IPlanAccessService
│   │   ├── PlanEditDtos.cs
│   │   ├── PlanShareController.cs      # Social S1: POST/DELETE /plans/:id/share (owner) + GET /plans/shared/:token (anon). Comparte=unlisted; el token es el capability, no el GUID
│   │   └── ShareTokenGenerator.cs      # share_token cripto-random URL-safe (12 bytes → Base64Url 16 chars, cabe en VARCHAR(16) de S0; 96 bits inadivinables)
│   ├── Profile/
│   │   ├── ProfileController.cs        # GET /me/profile, DELETE /me/profile
│   │   └── ProfileDtos.cs
│   ├── Routing/                        # Implementaciones (contratos en Shared/Routing/)
│   │   ├── MapboxRoutingService.cs     # Mapbox Directions API (IRoutingService)
│   │   └── RouteResolver.cs            # ISegmentResolver — caché de segmentos en RouteSegmentCache
│   │   # (Features/Social/ eliminado: las entidades sociales S0 viven ahora en Shared/Data/Entities/
│   │   #  junto al resto del modelo — Shared no debe depender de un slice. Aún sin endpoints)
│   ├── Taxonomy/
│   │   └── TaxonomyController.cs       # GET /taxonomy (categories + subcategories)
│   └── Waitlist/
│       ├── WaitlistController.cs       # POST /waitlist, GET /waitlist/count (anonymous, Landing proxy)
│       ├── WaitlistDtos.cs             # JoinWaitlistRequest, JoinWaitlistResponse, WaitlistCountResponse
│       ├── IEmailMarketingService.cs
│       └── KlaviyoService.cs           # Klaviyo email marketing integration
└── Shared/
    ├── Access/                         # Autorización centralizada de planes (S0)
    │   ├── IPlanAccessService.cs        # GetAccessAsync(planId, userId) → PlanAccess. Punto ÚNICO de autorización de planes
    │   ├── PlanAccessService.cs         # Reglas: owner→view+edit; collaborator editor→view+edit, viewer→view; visibility='public'→view (incl. anónimo); 'unlisted' NO por GUID; bloqueo↔owner niega. NO afloja ownership. Consumido por PlansController.GetPlan, PlanEditController, FollowController.StartSession (IDOR #116)
    │   └── PlanAccess.cs                # readonly record struct: PlanExists, CanView, CanEdit, IsOwner, Role
    ├── AI/
    │   ├── GeminiFileClient.cs                 # Gemini File API (subida resumable + poll ACTIVE + delete) para el import de vídeo
    │   ├── ImportOptions.cs                    # Config "Import" (modelo, límites, poll, ThirdPartyEnabled). Movido de Features/Import (lo consume GeminiFileClient, que vive aquí)
    │   ├── Security/                           # Infra de seguridad IA COMPARTIDA (Chat + Import). Movida de Features/Chat/Services (2 slices la usan)
    │   │   ├── InputNormalizer.cs              # Normaliza input hostil (homoglyph/zero-width/control tokens) antes de slot extraction / caption import
    │   │   ├── OutputSanitizer.cs              # Sanitiza texto de salida IA (quita URLs/markdown/HTML, escapa ángulos, cap)
    │   │   └── OutputValidator.cs              # Detecta drift/canary/identity-probe/injection en salida IA (+ CanaryToken)
    │   ├── Llm/                                # Cadena de fallback multi-proveedor (chat + builder)
    │   │   ├── ILlmClient.cs                   # LlmJsonRequest/LlmJsonResponse + interfaz
    │   │   ├── FallbackLlmClient.cs            # Encadena providers; limpia fences; valida JSON
    │   │   ├── LlmProviderHealthRegistry.cs    # Circuit breaker: 3 fallos seguidos → skip 60s
    │   │   ├── LlmClientFactory.cs             # Construye la cadena desde Llm:Providers
    │   │   ├── LlmOptions.cs                   # Config binding de Llm:Providers
    │   │   ├── LlmDiagnostics.cs               # Truncados compartidos
    │   │   └── Providers/                      # GeminiLlmClient, OpenAiCompatibleLlmClient (OpenAI+Mistral), AnthropicLlmClient
    │   └── Services/
    │       ├── IPlaceTranslatorService.cs      # TranslatePlaceAsync, TranslatePlanAsync
    │       ├── IDescriptionGeneratorService.cs # GeneratePlaceDescriptionAsync + WithDiagnostics
    │       ├── IPlanGenerationService.cs       # GenerateAsync, ResolveStopPlaces
    │       ├── ISchedulingService.cs           # Contrato cross-slice del scheduler (BuildPlanScheduleAsync). Impl = SchedulingService (Builder); lo consume Import T4 sin acoplarse al slice
    │       ├── IPlanNamingService.cs           # Contrato cross-slice del naming de plan (BuildPlanName). Impl = PlanNamingProvider (Builder, delega en el helper estático PlanNamingService)
    │       ├── PlaceTranslatorService.cs       # Implementación (movida de Builder/Services/)
    │       ├── DescriptionGeneratorService.cs  # Implementación (movida de Builder/Services/)
    │       └── EmbeddingService.cs             # Gemini embeddings para RAG (movida de Builder/Services/)
    ├── Auth/
    │   ├── AdminAuthorizeAttribute.cs   # Admin authorization attribute
    │   ├── AdminAuthorizationFilter.cs  # Admin role check via email domain
    │   ├── AdminClaimsExtensions.cs     # Extensions para claims admin
    │   ├── AuthSchemes.cs              # Constantes de nombre de scheme
    │   ├── RequireProAttribute.cs       # [RequirePro] — gate binario de tier (los endpoints de generación usan PlanGenerationGateService)
    │   ├── RequireProAuthorizationFilter.cs  # Valida tier RE-CONSULTANDO la DB (no el claim `tier` del JWT, vida 15 min)
    │   └── FirebaseUserExtensions.cs    # GetFirebaseUid(), GetEmail(), GetUserIdAsync()
    ├── Constants/
    │   ├── PlanLimits.cs               # MaxStopsPerDay + MaxPlanDurationDays (hard cap 14, fuente única del [Range] de días)
    │   ├── Tiers.cs                    # Pro/Free — fuente única del literal de tier (antes copiado en ~6 ficheros)
    │   └── PriceRanges.cs              # Rangos de precio normalizados
    ├── Coverage/                       # Gate de ciudades en vivo (contrato cross-slice)
    │   ├── ICityCoverageService.cs      # IsLive(city) + LiveCities (impl en Features/Cities/)
    │   └── CoverageOptions.cs           # Section name + default allowlist (["Miami"])
    ├── Data/
    │   ├── LocalListDbContext.cs        # EF Core DbContext, entity configs, indices
    │   ├── DesignTimeDbContextFactory.cs
    │   ├── PostgresErrorPredicates.cs   # IsUniqueViolation/IsForeignKeyViolation (23505/23503) — compartidos por Favorites e Import (antes controller→controller cross-slice)
    │   └── Entities/                   # EF Core entities
    │       ├── User.cs                  # firebase_uid (legado), google_user_id, apple_user_id, password_hash
    │       ├── UserProfile.cs           # Perfil PRIVADO (preferencias de viaje). ≠ Features/Social UserPublicProfile
    │       ├── RefreshToken.cs          # Tokens de refresh rotados (SHA-256 hash)
    │       ├── Plan.cs                   # visibility (private|unlisted|public) = fuente de verdad de autorización (S0); is_public espejo sincronizado 1-2 releases (setters bidireccionales; DTO deriva isPublic de visibility). +share_token único, revision, likes_count, published_at, cloned_from, moderation_locked. +imported_from_platform/imported_creator_handle (F2 T4, atribución de import; distintos de cloned_from). Config social en LocalListDbContext.ConfigureSocial
    │       ├── PlanStop.cs
    │       ├── PlanMetric.cs            # Métricas de generación (latencia, coste, señales)
    │       ├── Place.cs
    │       ├── FollowSession.cs
    │       ├── WaitlistEntry.cs
    │       ├── City.cs
    │       ├── CityRequest.cs           # Petición de cobertura (feedback selector). Texto INERTE (máx 100, validado por dominio). user_id FK SET NULL (invitado + sobrevive borrado). normalized_city agrupa variantes
    │       ├── Subcategory.cs
    │       ├── ChatSession.cs           # Sesión de chat slot-filling
    │       ├── ChatTurn.cs             # Turno individual de chat (diagnósticos AI)
    │       ├── VideoImportMetric.cs     # Diagnóstico del import de vídeo (tokens/coste/resultado + city/country/language extraídos como contexto de mercado + num_matched de T3; sin FK, sin vídeo/URIs/nombres de sitios/uploader)
    │       ├── BillingEvent.cs          # Ledger idempotencia webhooks RevenueCat (rc_event_id UNIQUE)
    │       ├── UsageCounter.cs          # Contador de uso (user, feature, period_start) — increment atómico vía UsageCounterService
    │       ├── Favorite.cs              # Favorito de sitio (user_id, place_id) PK compuesta = índice único (idempotencia vía 23505); ambos FK CASCADE (GDPR + borrado de place); índice (user_id, created_at DESC) para el listado
    │       ├── RouteSegmentCache.cs    # Caché de segmentos de ruta Mapbox
    │       │   # Entidades sociales S0 (movidas de Features/Social/Entities/; schema fijado por [Table]/[Column], sin migración):
    │       ├── UserPublicProfile.cs    # Perfil PÚBLICO (handle citext único, avatar, contadores). SEPARADO de UserProfile (privado). Creación LAZY
    │       ├── UserFollow.cs           # Grafo de follows. PK (follower_id, followee_id), CHECK no-self. NUNCA "Follow" (colisiona con Follow Mode)
    │       ├── PlanCollaborator.cs     # Co-edición. PK (plan_id, user_id), role editor|viewer. Owner NO es fila (sigue en plans.created_by)
    │       ├── PlanInvite.cs           # Invitación por token a colaborar (expira, max_uses, revocable)
    │       ├── ActivityEvent.cs        # Feed append-only. object_id polimórfico (sin FK), UNIQUE (actor, verb, object) idempotente
    │       ├── PlanLike.cs             # Like. PK (plan_id, user_id). Contador denormalizado en plans.likes_count
    │       ├── ContentReport.cs        # Reporte de moderación. reporter_id FK SET NULL (sobrevive borrado de cuenta)
    │       └── UserBlock.cs            # Bloqueo. PK (blocker_id, blocked_id). Consumido por PlanAccessService (bloqueo ↔ owner niega CanView)
    ├── I18n/
    │   └── LanguageAccessor.cs         # Resolución de idioma por Accept-Language / query param
    ├── Observability/
    │   ├── AiCallDiagnostics.cs        # DTO diagnósticos de llamadas Gemini (tokens, coste, latencia)
    │   ├── GeminiCostCalculator.cs     # Cálculo de coste por tokens
    │   └── PiiRedactor.cs              # Redacción de PII en logs y excerpts
    ├── PostHog/
    │   └── PostHogService.cs           # PostHog analytics (Capture, Identify, Alias)
    ├── Dtos/
    │   ├── PlaceDto.cs                  # PlaceDto (cross-slice, usado por Places + Plans). Photos sintetiza el proxy de fotos (nunca reemite URL de Google con key) + campo photoSource
    │   ├── PlacePhotoUrls.cs            # Punto único de síntesis Photos/photoSource para un Place, compartido por PlaceDto y ResolvedPlaceDto. SanitizeForStorage() limpia URLs de Google/preview-admin antes de persistir en cualquier ruta de escritura de Place.Photos
    │   ├── OpeningHours.cs              # OpeningHoursData, OpeningPeriod, OpeningTime
    │   ├── TripContextDto.cs            # Contexto de viaje (Builder + Chat)
    │   ├── ExtractedPreferences.cs      # Preferencias extraídas por Gemini
    │   ├── ScheduledStopDto.cs          # ScheduledStopDto, TravelInfoDto, ScheduleResult
    │   ├── ScheduledStopResult.cs       # ScheduledStopResult + ResolvedPlaceDto (Photos vía PlacePhotoUrls, mismo fix que PlaceDto)
    │   ├── PlanGenerationResult.cs      # Resultado del pipeline de generación
    │   └── PlanRouteSegmentDto.cs       # Segmento de ruta (Plans + Routing)
    ├── Routing/                        # Contratos cross-slice (impl en Features/Routing/)
    │   ├── IRoutingService.cs           # GetRouteAsync (Mapbox)
    │   ├── ISegmentResolver.cs          # ResolveAsync (batch) + ResolveSegmentAsync
    │   └── RoutingDtos.cs               # GeoPoint, RouteSegment, RoutingMode
    ├── Search/
    │   └── LikePatterns.cs             # Helpers para LIKE patterns en EF Core
    ├── Startup/                        # Extension methods del composition root (llamados desde Program.cs)
    │   ├── DatabaseServiceExtensions.cs    # AddPostgresDatabase (parse URL, pgvector, DbContext + factory)
    │   ├── DomainServiceExtensions.cs      # AddDomainServices (AI, routing, LLM chain, chat, posthog, taxonomy)
    │   ├── AuthenticationExtensions.cs     # AddJwtAuthentication (multi-scheme JWT + app auth services)
    │   ├── CorsExtensions.cs               # AddCorsPolicy
    │   └── RateLimitingExtensions.cs       # AddRateLimitingPolicies
    ├── Usage/                          # F4 — gates del catálogo Plus (cross-slice: Chat + Builder)
    │   ├── TierGate.cs                  # Lectura del tier FRESCO de DB (GetFreshTierAsync/IsPro/IsProAsync) — patrón compartido por Import/Favorites/Plans/generación
    │   ├── IUsageCounterService.cs      # TryConsumeAsync/GetUsedAsync — consumo atómico por (user, feature, periodo)
    │   ├── UsageCounterService.cs       # INSERT … ON CONFLICT … WHERE count < limit en 1 statement (sin ventana RMW)
    │   ├── IPlanGenerationGateService.cs # CheckAndConsumeAsync + PlanGateResult/PlanGateRejection
    │   └── PlanGenerationGateService.cs # Catálogo Plus: 3/mes free, 50/día pro, duración por tier (cupo de guardados vive en POST /plans, no aquí)
    └── Taxonomy/
        ├── ITaxonomyService.cs
        ├── PlaceTaxonomy.cs            # Árbol de categorías/subcategorías
        └── TaxonomyService.cs
```

## Scaling invariants

Railway despliega **una sola réplica** de esta API. Escalar a 2+ réplicas rompe silenciosamente lo siguiente:

| Componente | Tipo | Consecuencia con 2+ réplicas |
|---|---|---|
| Rate limiters (`AddRateLimiter`) | `IMemoryCache` in-process | Límites efectivos se multiplican por el número de réplicas |
| `IMemoryCache` (JWKS cache, etc.) | In-process | Cada réplica llena su propia caché — no hay coherencia |
| `SemaphoreSlim(4)` en `RouteResolver.FetchAndPersistAsync` | Per-call (variable local) | El semáforo no coordina entre réplicas; posibles ráfagas Mapbox |
| `SemaphoreSlim(4)` en `SchedulingService.PrefetchDaySegmentsAsync` | Per-call (variable local) | Ídem |
| `PhotoBudgetCounter` (breaker de presupuesto diario del proxy de fotos, `GooglePlaces:PhotoDailyBudgetCap`) | Contador in-process con reset por día UTC | Cada réplica cuenta su propio presupuesto → el cap efectivo de llamadas `/media` de pago se multiplica por el número de réplicas |

Antes de habilitar múltiples réplicas: migrar rate limiting a Redis (`AddStackExchangeRedisRateLimiting`) y reemplazar `IMemoryCache` por `IDistributedCache`.

## Endpoints

| Feature | Endpoints |
|---|---|
| Account | `GET /account` (+ `aiPlansMonth` cuota mensual y `importThirdPartyEnabled` capability F2), `DELETE /account` |
| Billing | `POST /webhooks/revenuecat` (anonymous, verifica header `Authorization` vs secreto; escribe `User.Tier` idempotente + reorder-safe) |
| Auth (admin / Firebase) | `POST /auth/sync` (Firebase token required) |
| Auth (app / HS256) | `POST /auth/signin` (provider=apple\|google + idToken), `POST /auth/register` (email+password), `POST /auth/login` (email+password), `POST /auth/refresh` (refresh token rotation) |
| Builder | `POST /builder/chat` (auth requerida desde F4; gates del catálogo Plus) |
| Chat | `POST /chat/turn` (anonymous), `POST /chat/generate` (auth requerida desde F4; gates del catálogo Plus), `DELETE /chat/session/:id` |
| Cities | `GET /cities/search`, `GET /cities/live` (allowlist de cobertura `Coverage:LiveCities`), `POST /cities`, `POST /cities/request` (anonymous; feedback "¿No ves tu ciudad?", `CityRequestLimit`) |
| Favorites | `PUT /favorites/:placeId` (favorita, idempotente; 404 opaco si el place no existe/no publicado; 403 `favorites_limit_reached` en free con ≥50 favoritos de places PUBLICADOS — misma semántica que el GET: lo que ves = lo que cuenta), `DELETE /favorites/:placeId` (desfavorita, idempotente → 204), `GET /favorites` (paginado `limit`/`offset`, PlaceDto ordenado `created_at DESC` + tiebreaker `place_id DESC`, solo publicados), `GET /favorites/ids` (ids ligeros para pintar corazones). Todos `[Authorize]` AppScheme (anónimo → 401) |
| Follow | `POST /follow/start`, `GET /follow/active`, `PATCH /follow/:id/next`, `/skip`, `/pause`, `/complete` |
| Places | `GET /places/`, `GET /places/:id`, `GET /places/:id/photos/:index` (anonymous; 302 al CDN de Google, key server-side, `PhotoLimit`) |
| Plans | `GET /plans/` (listado público: filtra por `visibility=='public'`, un `unlisted` JAMÁS aparece), `GET /plans/mine`, `GET /plans/:id`, `POST /plans` (crea plan de usuario; gate del cupo de guardados free = 5), `PUT /plans/:id/stops` (reemplazo atómico de stops, día ≤14), `DELETE /plans/:id` |
| Plans — Social S1 (share-link) | `POST /plans/:id/share` (`[Authorize]` AppScheme, SOLO owner vía `IPlanAccessService.IsOwner`; genera `share_token` cripto-random URL-safe si no existe y sube `private→unlisted`; idempotente = mismo token, no re-baja; 404 opaco si no owner; `{shareToken, visibility}`) · `DELETE /plans/:id/share` (owner; revoca = `share_token=null` y `unlisted→private`; `public` sigue public sin token; idempotente 204) · `GET /plans/shared/:token` (`[AllowAnonymous]` + `SharedPlanLimit` 60/min por IP; resuelve POR TOKEN, nunca por GUID; `PlanDetailDto` solo-lectura si `visibility ∈ {unlisted,public}` y coincide, con `createdById` EXCLUIDO por privacidad; token inválido/revocado/plan-vuelto-a-private → 404 indistinguible) |
| Profile | `GET /me/profile`, `DELETE /me/profile` |
| Taxonomy | `GET /taxonomy` |
| Import | `POST /import/video?platform=&creatorHandle=` (F2 T1+T3; `[Authorize]` AppScheme + gate Plus; metadatos por query = gating terceros pre-body, multipart solo-fichero 150 MB solo aquí; cuota 30/mes·10/día → `429 import_limit_reached`, reembolso solo si Gemini NO facturó; `ImportLimit` 20/hr por IP; T3 enriquece cada candidato con `matchedPlaceId`/`matchedPlaceName`/`matchConfidence` contra el catálogo published de la ciudad) · `POST /import/plan` (F2 T4; `[Authorize]` + gate Plus, SIN cuota nueva pero `ImportLimit` 20/hr por IP compartido con T1; crea el plan desde los placeIds confirmados — validación atómica/opaca `import_invalid_places` (incl. TOCTOU 23503), gating terceros, `days` clamp, dedup+orden canónico; scheduler determinista + reconcile no-loss; `source=imported` private; atribución `imported_from_platform`/`imported_creator_handle`; `201` con `PlanDetailDto`) |
| Waitlist | `POST /waitlist` (anonymous), `GET /waitlist/count` (anonymous) |
| Admin — Places | `GET /admin/places/cities`, `POST /admin/places/google-search`, `GET /admin/places/photo-preview` (preview de foto de Google pre-guardado por googlePlaceId+index, 302 con key server-side vía `IPlacePhotoService` de T1, nunca la expone al admin), `GET /admin/places`, `GET /admin/places/:id`, `POST /admin/places`, `POST /admin/places/bulk`, `POST /admin/places/import-from-urls`, `PATCH /admin/places/:id`, `PATCH /admin/places/:id/review`, `PATCH /admin/places/:id/postpone`, `DELETE /admin/places/:id`, `POST /admin/places/reindex-embeddings`, `POST /admin/places/backfill-opening-hours`, `POST /admin/places/:id/translate`, `POST /admin/places/:id/suggest-description`, `POST /admin/places/backfill-descriptions`, `POST /admin/places/translate-batch` |
| Admin — Plans | `GET /admin/plans`, `POST /admin/plans`, `POST /admin/plans/bulk`, `GET /admin/plans/:id`, `PATCH /admin/plans/:id` (metadata; con campo `stops` escribe metadata+stops atómico en 1 transacción), `POST /admin/plans/:id/translate`, `POST /admin/plans/translate-batch`, `PUT /admin/plans/:id/stops` (deprecado — usar PATCH atómico), `DELETE /admin/plans/:id` |
| Admin — Analytics | `GET /admin/analytics/chat-turns`, `GET /admin/analytics/chat-turns/stats`, `GET /admin/analytics/plan-metrics`, `GET /admin/analytics/plan-metrics/stats` |
| Admin — Cities | `DELETE /admin/cities/:id` |
| Admin — Subcategories | `GET /admin/subcategories`, `POST /admin/subcategories`, `PATCH /admin/subcategories/:id`, `DELETE /admin/subcategories/:id` |

## Auth — notas migratorias

- Usuarios con `firebase_uid` poblado son legado del periodo en que la app usó Firebase (PR #15). PR #29 portó los 4 endpoints HS256 desde `locallist-api-DEPRECATED`; la app ya no usa Firebase.
- `AppAuthController.Signin` (L81-85) busca al usuario por `{apple,google}_user_id` **OR por email** → un usuario legado con solo `firebase_uid` se enlaza al volver a iniciar sesión (se le pobla `google_user_id`/`apple_user_id`). `User.Id` (Guid) persiste, así que sus `Plan`/`PlanStop`/`FollowSession` siguen conectados.
- `firebase_uid` ya no se usa en el flujo nuevo (dead data en filas antiguas). No quitar la columna — sirve como trace de origen.
