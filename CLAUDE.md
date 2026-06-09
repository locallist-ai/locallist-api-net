# LocalList.API.NET

Parent context: see `../CLAUDE.md` for brand, domain concepts, and conventions.

When the user says "backend", "api", "net", ".net", or "c#", they mean this active project (`LocalList.API.NET`).

| | Details |
|---|---|
| **Tech** | .NET 10 (Controllers), C#, Entity Framework Core, Railway PostgreSQL |
| **Architecture** | Vertical Slice Architecture (VSA) — feature folders |
| **Deploy** | Railway (Dockerfile) |
| **Auth** | Dual-scheme JWT multi-issuer: `AppScheme` HS256 (app B2C, issuer `locallist-api`) + `FirebaseScheme` RS256 JWKS (admin interno). El scheme se selecciona por el `iss` del token en `Program.cs:218-246`. |
| **AI** | Gemini 2.5 Flash (`gemini-2.5-flash`) via `Features/Builder/AiProviderService.cs`. |
| **Rate Limit** | 100 req/min global. Auth 10/15min. Builder 5/hr. Chat 20/hr anon · 40/hr auth (sliding). Waitlist 5/60s. Admin 60/min. |

## Running Locally

```bash
cd LocalList/LocalList.API.NET
dotnet restore
dotnet run
```

Required User Secrets / Environment Variables:

**Core (requeridos — la app no arranca sin ellos)**
- `ConnectionStrings__DefaultConnection` — Postgres URL (Railway privada; nunca exponer públicamente)
- `FIREBASE_PROJECT_ID` — leído como env var directo (no doble guion bajo)
- `JWT_SECRET` — HS256 signing key ≥32 bytes. Leído como env var directo `JWT_SECRET`; fallback config key `Jwt:Secret` (`Jwt__Secret` en user-secrets).

**Gemini (Builder + Chat + RAG embeddings)**
- `Gemini__ApiKey` — si falta, fallback a keywords (graceful, no error)
- `Gemini__EmbeddingModel` — `gemini-embedding-001` (768 dims, L2-norm). **No** `text-embedding-004` (retirado 2026-01-14). Se usa en `EmbeddingService` para RAG.

**Google Places (admin ingestion)**
- `GooglePlaces__ApiKey` — Google Places API (New) key. Si no está, `POST /admin/places/google-search` devuelve 404 graceful.

**Routing**
- `Mapbox__AccessToken` — Si no está, routing desactivado (graceful warning).

**Analytics / Marketing (opcionales)**
- `PostHog__ApiKey` — Si no está, eventos PostHog silenciados.
- `PostHog__Host` — Default: `https://eu.i.posthog.com`.
- `Klaviyo__ApiKey` + `Klaviyo__WaitlistListId` — Si no están, email marketing de waitlist silenciado.

**Fase 3 — Video import (pendiente, sin plan activo)**
- Sin Apify. Arquitectura prevista: video file → Gemini multimodal File API directo.

## Project Structure (VSA)

```
LocalList.API.NET/
├── Program.cs                          # App config, DI, JWT, CORS, rate limiting
├── Features/
│   ├── Account/
│   │   └── AccountController.cs        # GET /account, DELETE /account
│   ├── Admin/
│   │   ├── Analytics/
│   │   │   ├── AdminChatTurnsController.cs    # GET /admin/analytics/chat-turns
│   │   │   └── AdminPlanMetricsController.cs  # GET /admin/analytics/plan-metrics
│   │   ├── Cities/
│   │   │   └── AdminCitiesController.cs       # CRUD /admin/cities
│   │   ├── Places/
│   │   │   ├── AdminPlacesController.cs       # CRUD + backfill /admin/places
│   │   │   └── GooglePlacesService.cs         # Google Places API (New) client
│   │   ├── Plans/
│   │   │   └── AdminPlansController.cs        # CRUD + translate /admin/plans
│   │   └── Subcategories/
│   │       └── AdminSubcategoriesController.cs # CRUD /admin/subcategories
│   ├── Auth/
│   │   ├── AuthController.cs           # POST /auth/sync (Firebase token → user sync, admin)
│   │   ├── AppAuthController.cs        # POST /auth/signin|register|login|refresh (app HS256)
│   │   ├── AuthDtos.cs                 # Sync/Signin/Register/Login/Refresh DTOs
│   │   └── Services/
│   │       ├── JwtTokenService.cs          # HS256 access token issuer (15min lifetime)
│   │       ├── RefreshTokenService.cs      # SHA-256 refresh rotation (30d lifetime)
│   │       ├── PasswordHasher.cs           # bcrypt para email/password
│   │       ├── GoogleIdTokenValidator.cs   # Valida ID token Google vs JWKS
│   │       ├── AppleIdTokenValidator.cs    # Valida ID token Apple vs JWKS
│   │       └── JwksRetriever.cs            # Caché JWKS para Apple
│   ├── Builder/
│   │   ├── BuilderController.cs        # POST /builder/chat
│   │   ├── BuilderDtos.cs             # BuilderChatRequest, ExtractedPreferences, TripContextDto
│   │   ├── AiProviderService.cs       # Gemini 2.5 Flash: prefs extraction, translation, descriptions
│   │   └── Services/
│   │       ├── EmbeddingService.cs         # Gemini embedding-001 para RAG
│   │       ├── PlaceRankingService.cs      # Reranking ponderado (RAG + scoring)
│   │       ├── PlanGenerationService.cs    # Orquesta RAG → ranking → scheduling
│   │       ├── SchedulingService.cs        # Scheduler determinista por semilla + WalkDayClock
│   │       └── PlanNamingService.cs        # Genera nombre del plan
│   ├── Chat/
│   │   ├── ChatController.cs           # POST /chat/turn, POST /chat/generate, DELETE /chat/session/:id
│   │   ├── ChatDtos.cs
│   │   └── Services/
│   │       ├── ChatAgentService.cs         # Slot-filling agent (multi-turn)
│   │       ├── SlotExtractorService.cs     # Gemini: extrae slots de intención de viaje
│   │       └── ChatSecLogger.cs            # Logging de seguridad para inputs de chat
│   ├── Cities/
│   │   ├── CitiesController.cs         # GET /cities/search (anon), POST /cities (auth)
│   │   └── CityNameNormalizer.cs       # Unicode FormD normalization para búsqueda
│   ├── Follow/
│   │   ├── FollowController.cs         # POST /follow/start, GET /active, PATCH next/skip/pause/complete
│   │   └── FollowDtos.cs              # FollowStartRequest
│   ├── Places/
│   │   └── PlacesController.cs         # GET /places, GET /places/:id
│   ├── Plans/
│   │   ├── PlansController.cs          # GET /plans, GET /plans/:id
│   │   └── PlanEditController.cs       # PUT /plans/:id/stops, DELETE /plans/:id (auth)
│   ├── Profile/
│   │   ├── ProfileController.cs        # GET /me/profile, PUT /me/profile, DELETE /me/profile
│   │   └── ProfileDtos.cs
│   ├── Routing/
│   │   ├── MapboxRoutingService.cs     # Mapbox Directions API client
│   │   └── RouteResolver.cs            # Batch route caching → route_segment_cache table
│   ├── Taxonomy/
│   │   └── TaxonomyController.cs       # GET /taxonomy (anon — categorías + subcategorías)
│   └── Waitlist/
│       ├── WaitlistController.cs       # POST /waitlist, GET /waitlist/count (anonymous, Landing proxy)
│       ├── KlaviyoService.cs           # Klaviyo email marketing integration
│       └── WaitlistDtos.cs             # JoinWaitlistRequest, JoinWaitlistResponse, WaitlistCountResponse
└── Shared/
    ├── Auth/
    │   ├── AdminAuthorizeAttribute.cs   # Admin authorization attribute
    │   ├── AdminAuthorizationFilter.cs  # Admin role check via email domain
    │   ├── AdminClaimsExtensions.cs     # GetAdminEmail() helpers
    │   ├── AuthSchemes.cs               # Constantes FirebaseScheme / AppScheme / MultiScheme
    │   └── FirebaseUserExtensions.cs    # GetFirebaseUid(), GetEmail(), GetUserIdAsync()
    ├── Constants/
    │   ├── PlanLimits.cs                # MaxStopsPerDay, MaxDays, etc.
    │   └── PriceRanges.cs               # Mapeo precio → rango display
    ├── Data/
    │   ├── LocalListDbContext.cs        # EF Core DbContext, entity configs, indices
    │   └── Entities/
    │       ├── User.cs                  # firebase_uid (legado), google_user_id, apple_user_id, password_hash
    │       ├── RefreshToken.cs          # Tokens de refresh rotados (SHA-256 hash)
    │       ├── UserProfile.cs           # Preferencias de usuario (nombre, avatar, etc.)
    │       ├── Plan.cs
    │       ├── PlanStop.cs
    │       ├── PlanMetric.cs            # Métricas de uso de plan (follows, completions)
    │       ├── Place.cs
    │       ├── FollowSession.cs
    │       ├── WaitlistEntry.cs
    │       ├── ChatSession.cs           # Sesión multi-turn del chat agent
    │       ├── ChatTurn.cs              # Turn individual dentro de una ChatSession
    │       ├── City.cs                  # Registro público de ciudades (searchable)
    │       ├── RouteSegmentCache.cs     # Caché de segmentos de ruta Mapbox
    │       └── Subcategory.cs           # Subcategorías de lugares (taxonomía editorial)
    ├── I18n/
    │   └── LanguageAccessor.cs          # Detecta idioma del request (Accept-Language)
    ├── Observability/
    │   ├── AiCallDiagnostics.cs         # Structured logging para llamadas a Gemini
    │   ├── GeminiCostCalculator.cs      # Estimación de tokens/coste por llamada
    │   └── PiiRedactor.cs               # Redacta PII antes de loggear inputs de usuario
    ├── PostHog/
    │   └── PostHogService.cs            # PostHog analytics event tracking
    ├── Search/
    │   └── LikePatterns.cs              # Helpers para LIKE patterns en EF queries
    └── Taxonomy/
        ├── ITaxonomyService.cs
        ├── PlaceTaxonomy.cs             # Categorías/subcategorías hardcoded (fuente de verdad)
        └── TaxonomyService.cs
```

## Endpoints

| Feature | Endpoints |
|---|---|
| Health | `GET /health` (anonymous) |
| Account | `GET /account`, `DELETE /account` |
| Auth (admin / Firebase) | `POST /auth/sync` (Firebase token required) |
| Auth (app / HS256) | `POST /auth/signin` (provider=apple\|google + idToken), `POST /auth/register` (email+password), `POST /auth/login` (email+password), `POST /auth/refresh` (refresh token rotation) |
| Profile | `GET /me/profile`, `PUT /me/profile`, `DELETE /me/profile` |
| Places | `GET /places/`, `GET /places/:id` |
| Plans | `GET /plans/`, `GET /plans/:id` |
| Plan Edit | `PUT /plans/:id/stops`, `DELETE /plans/:id` |
| Builder | `POST /builder/chat` |
| Chat | `POST /chat/turn`, `POST /chat/generate`, `DELETE /chat/session/:id` |
| Cities | `GET /cities/search` (anonymous), `POST /cities` (auth) |
| Follow | `POST /follow/start`, `GET /follow/active`, `PATCH /follow/:id/next`, `/skip`, `/pause`, `/complete` |
| Taxonomy | `GET /taxonomy` (anonymous) |
| Waitlist | `POST /waitlist` (anonymous), `GET /waitlist/count` (anonymous) |
| Admin Places | `GET /admin/places`, `GET /admin/places/:id`, `POST /admin/places`, `POST /admin/places/bulk`, `POST /admin/places/import-from-urls`, `PATCH /admin/places/:id`, `PATCH /admin/places/:id/review`, `PATCH /admin/places/:id/postpone`, `DELETE /admin/places/:id`, `POST /admin/places/google-search`, `POST /admin/places/reindex-embeddings`, `POST /admin/places/backfill-opening-hours`, `POST /admin/places/backfill-descriptions`, `POST /admin/places/:id/translate`, `POST /admin/places/:id/suggest-description`, `POST /admin/places/translate-batch` |
| Admin Plans | `GET /admin/plans`, `GET /admin/plans/:id`, `POST /admin/plans`, `POST /admin/plans/bulk`, `PATCH /admin/plans/:id`, `PUT /admin/plans/:id/stops`, `POST /admin/plans/:id/translate`, `POST /admin/plans/translate-batch`, `DELETE /admin/plans/:id` |
| Admin Subcategories | `GET /admin/subcategories`, `POST /admin/subcategories`, `PATCH /admin/subcategories/:id`, `DELETE /admin/subcategories/:id` |
| Admin Cities | CRUD `/admin/cities` |
| Admin Analytics | `GET /admin/analytics/chat-turns`, `GET /admin/analytics/plan-metrics` |

## Verification

Ejecutar antes de cualquier PR. Todos los pasos deben pasar.

```bash
dotnet restore LocalList.API.slnx
dotnet build LocalList.API.slnx --no-restore        # typecheck + compilación
dotnet test LocalList.API.slnx --no-build           # ~215 tests xUnit v3 (secuencial; Testcontainers Postgres)
dotnet ef migrations has-pending-model-changes --project LocalList.API.NET.csproj
```

Usa `/verify` para ejecutar todo de una vez. Usa `/review-diff` para revisar una rama como staff engineer antes de abrir el PR.

## Auth — notas migratorias

- Usuarios con `firebase_uid` poblado son legado del periodo en que la app usó Firebase (PR #15). PR #29 portó los 4 endpoints HS256 desde `locallist-api-DEPRECATED`; la app ya no usa Firebase.
- `AppAuthController.Signin` (L64) busca al usuario por `{apple,google}_user_id` **OR por email** → un usuario legado con solo `firebase_uid` se enlaza al volver a iniciar sesión (se le pobla `google_user_id`/`apple_user_id`). `User.Id` (Guid) persiste, así que sus `Plan`/`PlanStop`/`FollowSession` siguen conectados.
- `firebase_uid` ya no se usa en el flujo nuevo (dead data en filas antiguas). No quitar la columna — sirve como trace de origen.
