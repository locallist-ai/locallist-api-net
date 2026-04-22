# LocalList.API.NET

Backend API for the LocalList travel curation platform, built in **.NET 10 (C#)** with Vertical Slice Architecture (VSA).

For detailed technical context, architecture decisions, and endpoint reference, see **`CLAUDE.md`**.

## Tech Stack
- **Framework**: .NET 10 (Web API)
- **Architecture**: Vertical Slice Architecture — feature folders under `Features/`
- **Database**: PostgreSQL on Railway (private network)
- **ORM**: Entity Framework Core (`Npgsql.EntityFrameworkCore.PostgreSQL`), idempotent raw-SQL migrations
- **Authentication**: **two parallel JWT schemes**
  - **Firebase RS256** (issued by Firebase, validated via JWKS): used by the internal admin tool (`locallist-admin`) via `POST /auth/sync`
  - **App HS256** (issued by this backend with `BCrypt.Net-Next` for passwords): used by the mobile app (`locallist-app`) via `POST /auth/signin|login|register|refresh`
  - Multi-scheme is wired in `Program.cs` and dispatched by the JWT's `iss` claim
- **AI**: Gemini 2.5 Flash via `Features/Builder/AiProviderService.cs`
- **Rate Limiting**: 100 req/min global, Builder 5/hr, Auth 10/15min, Waitlist 5/60s, Admin 60/min
- **Deploy**: Railway (Dockerfile)

## Getting Started

### Prerequisites
1. Install [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).
2. Install the EF Core CLI tools: `dotnet tool install --global dotnet-ef`

### Environment Setup
Required environment variables (live in Railway dashboard for production; for local dev use `dotnet user-secrets` or `appsettings.Development.json`):

| Variable | Used by | Purpose |
|---|---|---|
| `ConnectionStrings__DefaultConnection` | EF Core / Npgsql | PostgreSQL connection string |
| `FIREBASE_PROJECT_ID` | `Program.cs` (Firebase scheme) | Validates Firebase RS256 tokens (admin) |
| `JWT_SECRET` | `JwtTokenService` + `Program.cs` (App scheme) | Signs/validates app HS256 tokens (≥32 bytes) |
| `APPLE_BUNDLE_ID` | `AppleIdTokenValidator` | Audience expected on Apple Sign-In ID tokens (`app.locallist.ios`) |
| `GOOGLE_CLIENT_ID` | `GoogleIdTokenValidator` | Web OAuth client ID (accepted audience for Google Sign-In tokens) |
| `GOOGLE_IOS_CLIENT_ID` | `GoogleIdTokenValidator` | Optional — iOS OAuth client ID (added as accepted audience when set; required for iOS native sign-in via expo-auth-session) |
| `GOOGLE_ANDROID_CLIENT_ID` | `GoogleIdTokenValidator` | Optional — Android OAuth client ID (accepted audience when set) |
| `Gemini__ApiKey` | `AiProviderService` | Gemini 2.5 Flash API key |
| `Klaviyo__ApiKey` | `KlaviyoService` | Klaviyo email-marketing API key (waitlist sync) |

### Running the API
1. `dotnet restore`
2. `dotnet run`
3. Swagger UI at `https://localhost:<port>/swagger` (Development only).

## Auth endpoints reference

| Endpoint | Scheme | Notes |
|---|---|---|
| `POST /auth/sync` | Firebase RS256 (admin) | First-login user sync; called by `locallist-admin` after Firebase SDK login |
| `POST /auth/signin` | (anonymous → emits App HS256) | Apple/Google ID-token sign-in/up. Body: `{provider:"apple"\|"google", idToken, name?}` |
| `POST /auth/register` | (anonymous → emits App HS256) | Email + password registration. Body: `{email, password, name?}` (password ≥8 chars per NIST SP 800-63B) |
| `POST /auth/login` | (anonymous → emits App HS256) | Email + password login. Body: `{email, password}` |
| `POST /auth/refresh` | (anonymous → emits App HS256) | Rotates refresh tokens (single-use). Body: `{refreshToken}` |

App-issued tokens carry the user's id (Guid) directly in `sub`. `User.GetUserIdAsync` (in `Shared/Auth/`) detects HS256 vs RS256 by parsing `sub` as a Guid (HS256) or falling back to `firebase_uid` lookup (RS256).

## Debugging Guide

1. **"The database query is failing"**: Check `Shared/Data/LocalListDbContext.cs`. Ensure entity properties match PostgreSQL column names via `[Column("name")]` attributes.
2. **"App login returns 401"**: Set a breakpoint in `Features/Auth/AppAuthController.cs` (`Login`) and verify bcrypt hash + `JWT_SECRET` configured.
3. **"Apple/Google signin returns 401"**: The fake validator returns null for unknown idTokens — check `Features/Auth/Services/AppleIdTokenValidator.cs` (or Google) and verify `APPLE_BUNDLE_ID`/`GOOGLE_CLIENT_ID` match the audience in the incoming idToken.
4. **"Admin token rejected"**: Check `Program.cs` Firebase scheme — `FIREBASE_PROJECT_ID` must match the Firebase project that issued the token. The multi-scheme dispatch uses the `iss` claim.
