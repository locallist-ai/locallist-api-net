# LocalList.API.NET

Parent context: see `../LocalList/CLAUDE.md` for brand, domain concepts, and conventions.

When the user says "backend", "api", "net", ".net", or "c#", they mean this active project (`LocalList.API.NET`).

| | Details |
|---|---|
| **Tech** | .NET 10 (Controllers), C#, Entity Framework Core, Neon PostgreSQL |
| **Architecture** | Vertical Slice Architecture (VSA) — feature folders |
| **Deploy** | Railway (Dockerfile) |
| **Auth** | Firebase Auth (RS256 JWKS) — Apple Sign In + Google + email/password. Single `/auth/sync` endpoint. |
| **AI** | Gemini 2.5 Flash via `Features/Builder/AiProviderService.cs`. |
| **Rate Limit** | 100 req/min global. Builder limited to 5/hr. |

## Running Locally

```bash
cd LocalList/LocalList.API.NET
dotnet restore
dotnet run
```

Required User Secrets / Environment Variables:
`ConnectionStrings__DefaultConnection`, `FIREBASE_PROJECT_ID`, `Gemini__ApiKey`

## Project Structure (VSA)

```
LocalList.API.NET/
├── Program.cs                          # App config, DI, JWT, CORS, rate limiting
├── Features/
│   ├── Account/
│   │   └── AccountController.cs        # GET /account, DELETE /account
│   ├── Auth/
│   │   ├── AuthController.cs           # POST /auth/sync (Firebase token → user sync)
│   │   └── AuthDtos.cs                 # SyncUserDto, SyncResponse
│   ├── Builder/
│   │   ├── BuilderController.cs        # POST /builder/chat
│   │   ├── BuilderDtos.cs             # BuilderChatRequest, ExtractedPreferences, TripContextDto
│   │   └── AiProviderService.cs       # Gemini 2.5 Flash integration
│   ├── Follow/
│   │   ├── FollowController.cs         # POST /follow/start, GET /active, PATCH next/skip/pause/complete
│   │   └── FollowDtos.cs              # FollowStartRequest
│   ├── Places/
│   │   └── PlacesController.cs         # GET /places, GET /places/:id
│   └── Plans/
│       └── PlansController.cs          # GET /plans, GET /plans/:id
└── Shared/
    ├── Auth/
    │   ├── AdminAuthorizeAttribute.cs   # Admin authorization attribute
    │   ├── AdminAuthorizationFilter.cs  # Admin role check via email domain
    │   └── FirebaseUserExtensions.cs    # GetFirebaseUid(), GetEmail(), GetUserIdAsync()
    └── Data/
        ├── LocalListDbContext.cs        # EF Core DbContext, entity configs, indices
        └── Entities/                    # EF Core entities
            ├── User.cs                  # Includes firebase_uid column
            ├── Plan.cs
            ├── PlanStop.cs
            ├── Place.cs
            └── FollowSession.cs
```

## Endpoints

| Feature | Endpoints |
|---|---|
| Account | `GET /account`, `DELETE /account` |
| Auth | `POST /auth/sync` (Firebase token required) |
| Places | `GET /places/`, `GET /places/:id` |
| Plans | `GET /plans/`, `GET /plans/:id` |
| Builder | `POST /builder/chat` |
| Follow | `POST /follow/start`, `GET /follow/active`, `PATCH /follow/:id/next`, `/skip`, `/pause`, `/complete` |
