# LocalList.API.NET

Backend API for the LocalList travel curation platform, built in **.NET 10 (C#)** with Vertical Slice Architecture (VSA).

For detailed technical context, architecture decisions, and endpoint reference, see **`CLAUDE.md`**.

## Tech Stack
- **Framework**: .NET 10 (Web API)
- **Architecture**: Vertical Slice Architecture — feature folders under `Features/`
- **Database**: PostgreSQL (Neon Serverless)
- **ORM**: Entity Framework Core (`Npgsql.EntityFrameworkCore.PostgreSQL`)
- **Authentication**: Custom JWT Bearer Auth (HS256) + `Google.Apis.Auth` + `BCrypt.Net-Next`
- **AI**: Gemini 2.5 Flash via `Features/Builder/AiProviderService.cs`
- **Rate Limiting**: 100 req/min global, Builder limited to 5/hr
- **Deploy**: Railway (Dockerfile)

## Getting Started

### Prerequisites
1. Install [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).
2. Install the EF Core CLI tools: `dotnet tool install --global dotnet-ef`

### Environment Setup
Required User Secrets or Environment Variables:
- `ConnectionStrings__DefaultConnection` — Neon PostgreSQL connection string
- `Jwt__Secret` — JWT signing key
- `Gemini__ApiKey` — Gemini Flash API key

For local development, configure via `dotnet user-secrets` or `appsettings.Development.json`.

### Running the API
1. Open a terminal in this directory (`LocalList.API.NET`).
2. Run `dotnet restore` to fetch NuGet packages.
3. Run `dotnet run` (or press F5 in Visual Studio / VS Code).
4. Swagger UI available at `https://localhost:<port>/swagger`.

## Debugging Guide

1. **"The database query is failing"**: Check `Shared/Data/LocalListDbContext.cs`. Ensure entity properties match PostgreSQL column names via `[Column("name")]` attributes.
2. **"Login isn't working"**: Open `Features/Auth/AuthController.cs`, set a breakpoint on `[HttpPost("login")]`, and step through BCrypt validation.
3. **"Token is always invalid"**: Check `Shared/Auth/JwtTokenService.cs`. Verify `Issuer` and `Audience` match what the frontend expects, and ensure `Jwt:Secret` is configured.
