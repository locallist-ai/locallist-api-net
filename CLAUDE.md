# LocalList.API.NET

Parent context: see `../LocalList/CLAUDE.md` for brand, domain concepts, and conventions.

When the user says "backend", "api", "net", ".net", or "c#", they mean this active project (`LocalList.API.NET`).

| | Details |
|---|---|
| **Tech** | .NET 8 Core (Minimal APIs), C#, Entity Framework Core, Neon PostgreSQL |
| **Deploy** | Railway (Dockerfile) |
| **Auth** | Custom JWT (HS256) — Apple Sign In + Google + email/password. Password hashing with BCrypt. |
| **AI** | Gemini 2.5 Flash via `Services/AiProviderService.cs`. |
| **Rate Limit** | 100 req/min global. Builder limited to 5/hr. |

## Running Locally

```bash
cd LocalList/LocalList.API.NET
dotnet restore
dotnet run
```

Required User Secrets / Environment Variables:
`ConnectionStrings__DefaultConnection`, `Jwt__Secret`, `Gemini__ApiKey`

## Endpoints

| Controller | Endpoints |
|---|---|
| `AccountController.cs` | `GET /account`, `DELETE /account` |
| `AuthController.cs` | `POST /auth/login`, `/auth/register`, `/auth/oauth-login`, `/auth/refresh` |
| `PlacesController.cs` | `GET /places/`, `GET /places/:id` |
| `PlansController.cs` | `GET /plans/`, `GET /plans/:id` |
| `BuilderController.cs` | `POST /builder/chat` |
| `FollowController.cs` | `POST /follow/start`, `GET /follow/active`, `PATCH /follow/:id/next`, `/skip`, `/pause`, `/complete` |

## Key Files

- `Program.cs` — Main app configuration, DI container setup, JWT Bearer configuration, CORS, Database connection.
- `Data/LocalListDbContext.cs` — EF Core database context, entity configurations.
- `Models/` — EF Core Entities (User, Place, Plan, PlanStop, FollowSession, RefreshToken).
- `DTOs/` — Data Transfer Objects with DataAnnotation validations (crucial for security).
- `Services/JwtTokenService.cs` — JWT generation and refresh token management.
- `Services/AiProviderService.cs` — Gemini 2.5 integration for the Plan Builder.
- `Helpers/Haversine.cs` — Distance calculations between coordinates.
