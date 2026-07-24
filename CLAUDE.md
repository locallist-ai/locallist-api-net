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
| **Rate Limit** | 100 req/min global. Endpoints medidos (sliding window, techo por IP encadenado anti account-farming + refinamiento por identidad, bucket alto SOLO AppScheme): **builder/chat-generate** techo 60/hr por IP (`Builder__RateLimitPerHourPerIp`) + 5/hr anon · 20/hr auth (`Builder__RateLimitPerHour` / `__RateLimitPerHourAuthenticated`); **chat/turn** techo 120/hr por IP (`Chat__RateLimitTurnsPerHourPerIp`) + 20/hr anon · 40/hr auth (`Chat__RateLimitTurnsPerHourAnonymous` / `__Authenticated`). Auth 10/15min. Waitlist 5/60s. Admin 60/min. Photos 60/min por IP (`PhotoLimit`, `GooglePlaces__PhotoRateLimitPerMinute`). `UseRateLimiter` va después de `UseAuthentication`. |

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

**Routing (Mapbox)**
- `Mapbox__AccessToken` — opcional. Si no está, routing se deshabilita gracefully (stops sin `travelFromPrevious`).

**Analytics (PostHog)**
- `PostHog__ApiKey` — opcional. Eventos `plan_generated`, `user_signed_up`, `user_signed_in`, etc.
- `PostHog__Host` — opcional, por defecto `https://eu.i.posthog.com`.

**Email marketing (Klaviyo / Waitlist)**
- `Klaviyo__ApiKey` — opcional. Sin él, el servicio de email se deshabilita silenciosamente.
- `Klaviyo__WaitlistListId` — ID de lista de Klaviyo para la waitlist.

**Fase 3 — Video import (pendiente, sin plan activo)**
- Sin Apify. Arquitectura prevista: video file → Gemini multimodal File API directo.

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
│   │   │   ├── AdminPlacesController.cs       # CRUD + backfill + translate (ver Endpoints)
│   │   │   ├── GooglePlacesService.cs         # Google Places API (New) integration
│   │   │   ├── PlaceImportService.cs          # Lógica de ingesta extraída del controller
│   │   │   └── AdminDtos.cs
│   │   ├── Plans/
│   │   │   ├── AdminPlansController.cs        # CRUD + translate curated plans
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
│   ├── Builder/
│   │   ├── BuilderController.cs        # POST /builder/chat
│   │   ├── BuilderDtos.cs              # BuilderChatRequest
│   │   ├── Services/
│   │   │   ├── PreferenceExtractorService.cs   # Gemini → ExtractedPreferences
│   │   │   ├── PlaceRankingService.cs          # Reranking determinista ponderado
│   │   │   ├── PlanGenerationService.cs        # Orquesta RAG + prefs + scheduler
│   │   │   ├── PlanNamingService.cs            # Genera nombre y descripción del plan
│   │   │   └── SchedulingService.cs            # Scheduler determinista por semilla
│   │   └── Shared/
│   │       └── GroupTypePolicy.cs       # Reglas de capacidad por tipo de grupo
│   ├── Chat/
│   │   ├── ChatController.cs           # POST /chat/turn, /chat/generate, DELETE /chat/session/:id
│   │   ├── ChatDtos.cs
│   │   ├── I18n/
│   │   │   └── ChatStrings.cs
│   │   └── Services/
│   │       ├── ChatAgentService.cs         # Orquesta slot-filling + sesión + generación
│   │       ├── SlotExtractorService.cs     # Gemini → extrae slots de texto libre
│   │       ├── InputNormalizer.cs          # Normaliza input antes de slot extraction
│   │       ├── OutputSanitizer.cs          # Sanitiza respuesta AI
│   │       ├── OutputValidator.cs          # Valida estructura de respuesta AI
│   │       ├── PromptInjectionDetector.cs  # Detecta prompt injection en input
│   │       ├── JailbreakPatternLibrary.cs  # Patrones de jailbreak conocidos
│   │       ├── ResponseDriftDetector.cs    # Detecta drift off-topic en respuestas AI
│   │       ├── SuspicionTracker.cs         # Trackea sesiones sospechosas (rate de fallos)
│   │       └── ChatSecLogger.cs            # Log estructurado de eventos de seguridad
│   ├── Cities/
│   │   ├── CitiesController.cs         # GET /cities/search, GET /cities/live, POST /cities
│   │   ├── CityCoverageService.cs      # ICityCoverageService impl (allowlist Coverage:LiveCities)
│   │   └── CityNameNormalizer.cs       # Unicode FormD normalization para búsqueda
│   ├── Follow/
│   │   ├── FollowController.cs         # POST /follow/start, GET /active, PATCH next/skip/pause/complete
│   │   └── FollowDtos.cs              # FollowStartRequest
│   ├── Places/
│   │   ├── PlacesController.cs         # GET /places, GET /places/:id
│   │   └── Photos/                     # Proxy de fotos de Google (runtime-only, ToS-compliant)
│   │       ├── PlacePhotosController.cs  # GET /places/:id/photos/:index (302 al photoUri, key server-side)
│   │       ├── PlacePhotoService.cs      # Place Details (FieldMask=photos, gratis) + /media (key en header) → photoUri
│   │       └── PhotoBudgetCounter.cs     # Circuit breaker de presupuesto diario (in-process, reset UTC)
│   ├── Plans/
│   │   ├── PlansController.cs          # GET /plans, GET /plans/:id
│   │   ├── PlanDtos.cs
│   │   ├── PlanEditController.cs       # DELETE /plans/:id
│   │   └── PlanEditDtos.cs
│   ├── Profile/
│   │   ├── ProfileController.cs        # GET /me/profile, DELETE /me/profile
│   │   └── ProfileDtos.cs
│   ├── Routing/                        # Implementaciones (contratos en Shared/Routing/)
│   │   ├── MapboxRoutingService.cs     # Mapbox Directions API (IRoutingService)
│   │   └── RouteResolver.cs            # ISegmentResolver — caché de segmentos en RouteSegmentCache
│   ├── Taxonomy/
│   │   └── TaxonomyController.cs       # GET /taxonomy (categories + subcategories)
│   └── Waitlist/
│       ├── WaitlistController.cs       # POST /waitlist, GET /waitlist/count (anonymous, Landing proxy)
│       ├── WaitlistDtos.cs             # JoinWaitlistRequest, JoinWaitlistResponse, WaitlistCountResponse
│       ├── IEmailMarketingService.cs
│       └── KlaviyoService.cs           # Klaviyo email marketing integration
└── Shared/
    ├── AI/
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
    │       ├── PlaceTranslatorService.cs       # Implementación (movida de Builder/Services/)
    │       ├── DescriptionGeneratorService.cs  # Implementación (movida de Builder/Services/)
    │       └── EmbeddingService.cs             # Gemini embeddings para RAG (movida de Builder/Services/)
    ├── Auth/
    │   ├── AdminAuthorizeAttribute.cs   # Admin authorization attribute
    │   ├── AdminAuthorizationFilter.cs  # Admin role check via email domain
    │   ├── AdminClaimsExtensions.cs     # Extensions para claims admin
    │   ├── AuthSchemes.cs              # Constantes de nombre de scheme
    │   └── FirebaseUserExtensions.cs    # GetFirebaseUid(), GetEmail(), GetUserIdAsync()
    ├── Constants/
    │   ├── PlanLimits.cs               # Límites de stops por día, etc.
    │   └── PriceRanges.cs              # Rangos de precio normalizados
    ├── Coverage/                       # Gate de ciudades en vivo (contrato cross-slice)
    │   ├── ICityCoverageService.cs      # IsLive(city) + LiveCities (impl en Features/Cities/)
    │   └── CoverageOptions.cs           # Section name + default allowlist (["Miami"])
    ├── Data/
    │   ├── LocalListDbContext.cs        # EF Core DbContext, entity configs, indices
    │   ├── DesignTimeDbContextFactory.cs
    │   └── Entities/                   # EF Core entities
    │       ├── User.cs                  # firebase_uid (legado), google_user_id, apple_user_id, password_hash
    │       ├── UserProfile.cs           # Perfil extendido del usuario
    │       ├── RefreshToken.cs          # Tokens de refresh rotados (SHA-256 hash)
    │       ├── Plan.cs
    │       ├── PlanStop.cs
    │       ├── PlanMetric.cs            # Métricas de generación (latencia, coste, señales)
    │       ├── Place.cs
    │       ├── FollowSession.cs
    │       ├── WaitlistEntry.cs
    │       ├── City.cs
    │       ├── Subcategory.cs
    │       ├── ChatSession.cs           # Sesión de chat slot-filling
    │       ├── ChatTurn.cs             # Turno individual de chat (diagnósticos AI)
    │       └── RouteSegmentCache.cs    # Caché de segmentos de ruta Mapbox
    ├── I18n/
    │   └── LanguageAccessor.cs         # Resolución de idioma por Accept-Language / query param
    ├── Observability/
    │   ├── AiCallDiagnostics.cs        # DTO diagnósticos de llamadas Gemini (tokens, coste, latencia)
    │   ├── GeminiCostCalculator.cs     # Cálculo de coste por tokens
    │   └── PiiRedactor.cs              # Redacción de PII en logs y excerpts
    ├── PostHog/
    │   └── PostHogService.cs           # PostHog analytics (Capture, Identify, Alias)
    ├── Dtos/
    │   ├── PlaceDto.cs                  # PlaceDto (cross-slice, usado por Plans + Admin)
    │   ├── OpeningHours.cs              # OpeningHoursData, OpeningPeriod, OpeningTime
    │   ├── TripContextDto.cs            # Contexto de viaje (Builder + Chat)
    │   ├── ExtractedPreferences.cs      # Preferencias extraídas por Gemini
    │   ├── ScheduledStopDto.cs          # ScheduledStopDto, TravelInfoDto, ScheduleResult
    │   ├── ScheduledStopResult.cs       # ScheduledStopResult + ResolvedPlaceDto (tipado de ResolveStopPlaces)
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
| Account | `GET /account`, `DELETE /account` |
| Auth (admin / Firebase) | `POST /auth/sync` (Firebase token required) |
| Auth (app / HS256) | `POST /auth/signin` (provider=apple\|google + idToken), `POST /auth/register` (email+password), `POST /auth/login` (email+password), `POST /auth/refresh` (refresh token rotation) |
| Builder | `POST /builder/chat` |
| Chat | `POST /chat/turn`, `POST /chat/generate`, `DELETE /chat/session/:id` |
| Cities | `GET /cities/search`, `GET /cities/live` (allowlist de cobertura `Coverage:LiveCities`), `POST /cities` |
| Follow | `POST /follow/start`, `GET /follow/active`, `PATCH /follow/:id/next`, `/skip`, `/pause`, `/complete` |
| Places | `GET /places/`, `GET /places/:id`, `GET /places/:id/photos/:index` (anonymous; 302 al CDN de Google, key server-side, `PhotoLimit`) |
| Plans | `GET /plans/`, `GET /plans/:id`, `DELETE /plans/:id` |
| Profile | `GET /me/profile`, `DELETE /me/profile` |
| Taxonomy | `GET /taxonomy` |
| Waitlist | `POST /waitlist` (anonymous), `GET /waitlist/count` (anonymous) |
| Admin — Places | `GET /admin/places/cities`, `POST /admin/places/google-search`, `GET /admin/places`, `GET /admin/places/:id`, `POST /admin/places`, `POST /admin/places/bulk`, `POST /admin/places/import-from-urls`, `PATCH /admin/places/:id`, `PATCH /admin/places/:id/review`, `PATCH /admin/places/:id/postpone`, `DELETE /admin/places/:id`, `POST /admin/places/reindex-embeddings`, `POST /admin/places/backfill-opening-hours`, `POST /admin/places/:id/translate`, `POST /admin/places/:id/suggest-description`, `POST /admin/places/backfill-descriptions`, `POST /admin/places/translate-batch` |
| Admin — Plans | `GET /admin/plans`, `POST /admin/plans`, `POST /admin/plans/bulk`, `GET /admin/plans/:id`, `PATCH /admin/plans/:id` (metadata; con campo `stops` escribe metadata+stops atómico en 1 transacción), `POST /admin/plans/:id/translate`, `POST /admin/plans/translate-batch`, `PUT /admin/plans/:id/stops` (deprecado — usar PATCH atómico), `DELETE /admin/plans/:id` |
| Admin — Analytics | `GET /admin/analytics/chat-turns`, `GET /admin/analytics/chat-turns/stats`, `GET /admin/analytics/plan-metrics`, `GET /admin/analytics/plan-metrics/stats` |
| Admin — Cities | `DELETE /admin/cities/:id` |
| Admin — Subcategories | `GET /admin/subcategories`, `POST /admin/subcategories`, `PATCH /admin/subcategories/:id`, `DELETE /admin/subcategories/:id` |

## Auth — notas migratorias

- Usuarios con `firebase_uid` poblado son legado del periodo en que la app usó Firebase (PR #15). PR #29 portó los 4 endpoints HS256 desde `locallist-api-DEPRECATED`; la app ya no usa Firebase.
- `AppAuthController.Signin` (L81-85) busca al usuario por `{apple,google}_user_id` **OR por email** → un usuario legado con solo `firebase_uid` se enlaza al volver a iniciar sesión (se le pobla `google_user_id`/`apple_user_id`). `User.Id` (Guid) persiste, así que sus `Plan`/`PlanStop`/`FollowSession` siguen conectados.
- `firebase_uid` ya no se usa en el flujo nuevo (dead data en filas antiguas). No quitar la columna — sirve como trace de origen.
