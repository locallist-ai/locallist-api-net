# LocalList.API.NET

This is the central Backend API for the LocalList ecosystem, rewritten in **.NET 8 (C#)** for improved maintainability, debugging, and type safety. It replaces the legacy Node.js/Hono implementation.

## 🏗️ Architecture

The project follows a standard ASP.NET Core MVC / Web API pattern, structured to be lightweight but fully typed.

- **`Program.cs`**: The entry point. Configures Dependency Injection (DI), Middleware (CORS, JWT Authentication, Swagger), and routes.
- **`Data/`**: Contains the Entity Framework Core configuration.
  - **`Models/`**: The C# representations of our PostgreSQL tables (`User`, `Place`, `Plan`, etc.). These mirror the legacy Drizzle ORM schema exactly so no data is lost.
  - **`LocalListDbContext.cs`**: The EF Core database context. Configures relationships, cascades, and constraints.
- **`Controllers/`**: Contains the API endpoints.
  - **`AuthController.cs`**: Handles Apple/Google OAuth, standard Email/Password registration, and JWT issuance/refreshing.
- **`Services/`**: Contains business logic encapsulated for Dependency Injection.
  - **`JwtTokenService.cs`**: Handles the generation of Access and Refresh tokens.

## 🛠️ Tech Stack
- **Framework**: .NET 8 (Web API)
- **Database**: PostgreSQL (Neon Serverless)
- **ORM**: Entity Framework Core 8 (`Npgsql.EntityFrameworkCore.PostgreSQL`)
- **Authentication**: Custom JWT Bearer Auth + `Google.Apis.Auth` + `BCrypt.Net-Next`

## 🚀 Getting Started

### Prerequisites
1. Install [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
2. Install the EF Core CLI tools: `dotnet tool install --global dotnet-ef`

### Database Setup
Ensure your local PostgreSQL or Neon database connection string is placed in `appsettings.Development.json`:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Database=locallist_db;Username=postgres;Password=mypassword"
  }
}
```

### Running the API
1. Open a terminal in this directory (`LocalList.API.NET`).
2. Run `dotnet restore` to fetch NuGet packages.
3. Run `dotnet run` (or press F5 in Visual Studio).
4. The API will start, and you can view the Swagger UI at `https://localhost:<port>/swagger`.

## 🐛 Debugging Guide for the Solo Founder
The primary reason this stack exists is so you (the founder) can debug it easily using Visual Studio or VS Code C# extensions.

1. **"The database query is failing"**: Open `LocalListDbContext.cs`. Ensure the properties match the exact PostgreSQL column names using the `[Column("name")]` attribute. 
2. **"Login isn't working"**: Open `AuthController.cs`, put a breakpoint on `[HttpPost("login")]`, and step through the BCrypt validation.
3. **"Token is always invalid"**: Check `JwtTokenService.cs`. Verify the `Issuer` and `Audience` match what the frontend expects, and ensure `appsettings.json` has the correct `Jwt:Secret`.
