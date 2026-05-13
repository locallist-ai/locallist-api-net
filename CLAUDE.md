# LocalList.API.NET

Parent context: see `../LocalList/CLAUDE.md` for brand, domain concepts, and conventions.

When the user says "backend", "api", "net", ".net", or "c#", they mean this active project (`LocalList.API.NET`).

| | Details |
|---|---|
| **Tech** | .NET 10 (Controllers), C#, Entity Framework Core, Railway PostgreSQL |
| **Architecture** | Vertical Slice Architecture (VSA) — feature folders |
| **Deploy** | Railway (Dockerfile) |
| **Auth** | Dual-scheme JWT multi-issuer: `AppScheme` HS256 (app B2C, issuer `locallist-api`) + `FirebaseScheme` RS256 JWKS (admin interno). El scheme se selecciona por el `iss` del token en `Program.cs:123-131`. |
| **AI** | Gemini 2.5 Flash via `Features/Builder/AiProviderService.cs`. |
| **Rate Limit** | 100 req/min global. Builder 5/hr. Waitlist 5/60s. |

## Running Locally

```bash
cd LocalList/LocalList.API.NET
dotnet restore
dotnet run
```

Required User Secrets / Environment Variables:

**Core**
- `ConnectionStrings__DefaultConnection` — Postgres URL (Railway privada; nunca exponer públicamente)
- `FIREBASE_PROJECT_ID`
- `Jwt__Secret` — HS256 signing key para tokens de la app (≥32 bytes)

**Gemini (Builder + RAG embeddings)**
- `Gemini__ApiKey`
- `Gemini__EmbeddingModel` — por defecto `text-embedding-004` (768 dims). Se usa en `EmbeddingService` (RAG Fase 1) y en el pipeline de import reels (Fase 3).

**Google Places (admin ingestion — Fase A)**
- `GooglePlaces__ApiKey` — Google Places API (New) key. Activa en GCP: API "Places API (New)". Si no está, `POST /admin/places/google-search` devuelve 404 graceful.

**Fase 3 — Video import (pendiente, plan en ~/.claude/plans/)**
- Sin Apify. Arquitectura: video file → Gemini multimodal directo (File API). Ver plan `creo-que-lo-mejor-curious-fiddle.md`.

## Project Structure (VSA)

```
LocalList.API.NET/
├── Program.cs                          # App config, DI, JWT, CORS, rate limiting
├── Features/
│   ├── Account/
│   │   └── AccountController.cs        # GET /account, DELETE /account
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
│   │   ├── BuilderDtos.cs             # BuilderChatRequest, ExtractedPreferences, TripContextDto
│   │   └── AiProviderService.cs       # Gemini 2.5 Flash integration
│   ├── Follow/
│   │   ├── FollowController.cs         # POST /follow/start, GET /active, PATCH next/skip/pause/complete
│   │   └── FollowDtos.cs              # FollowStartRequest
│   ├── Places/
│   │   └── PlacesController.cs         # GET /places, GET /places/:id
│   ├── Plans/
│       └── PlansController.cs          # GET /plans, GET /plans/:id
│   └── Waitlist/
│       ├── WaitlistController.cs       # POST /waitlist, GET /waitlist/count (anonymous, Landing proxy)
│       └── WaitlistDtos.cs             # JoinWaitlistRequest, JoinWaitlistResponse, WaitlistCountResponse
└── Shared/
    ├── Auth/
    │   ├── AdminAuthorizeAttribute.cs   # Admin authorization attribute
    │   ├── AdminAuthorizationFilter.cs  # Admin role check via email domain
    │   └── FirebaseUserExtensions.cs    # GetFirebaseUid(), GetEmail(), GetUserIdAsync()
    └── Data/
        ├── LocalListDbContext.cs        # EF Core DbContext, entity configs, indices
        └── Entities/                    # EF Core entities
            ├── User.cs                  # firebase_uid (legado), google_user_id, apple_user_id, password_hash
            ├── RefreshToken.cs          # Tokens de refresh rotados (SHA-256 hash)
            ├── Plan.cs
            ├── PlanStop.cs
            ├── Place.cs
            ├── FollowSession.cs
            └── WaitlistEntry.cs
```

## Endpoints

| Feature | Endpoints |
|---|---|
| Account | `GET /account`, `DELETE /account` |
| Auth (admin / Firebase) | `POST /auth/sync` (Firebase token required) |
| Auth (app / HS256) | `POST /auth/signin` (provider=apple\|google + idToken), `POST /auth/register` (email+password), `POST /auth/login` (email+password), `POST /auth/refresh` (refresh token rotation) |
| Places | `GET /places/`, `GET /places/:id` |
| Plans | `GET /plans/`, `GET /plans/:id` |
| Builder | `POST /builder/chat` |
| Follow | `POST /follow/start`, `GET /follow/active`, `PATCH /follow/:id/next`, `/skip`, `/pause`, `/complete` |
| Waitlist | `POST /waitlist` (anonymous), `GET /waitlist/count` (anonymous) |

## Auth — notas migratorias

- Usuarios con `firebase_uid` poblado son legado del periodo en que la app usó Firebase (PR #15). PR #29 portó los 4 endpoints HS256 desde `locallist-api-DEPRECATED`; la app ya no usa Firebase.
- `AppAuthController.Signin` (L67-71) busca al usuario por `{apple,google}_user_id` **OR por email** → un usuario legado con solo `firebase_uid` se enlaza al volver a iniciar sesión (se le pobla `google_user_id`/`apple_user_id`). `User.Id` (Guid) persiste, así que sus `Plan`/`PlanStop`/`FollowSession` siguen conectados.
- `firebase_uid` ya no se usa en el flujo nuevo (dead data en filas antiguas). No quitar la columna — sirve como trace de origen.
