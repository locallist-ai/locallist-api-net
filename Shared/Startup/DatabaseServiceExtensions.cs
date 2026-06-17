using LocalList.API.NET.Shared.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pgvector.EntityFrameworkCore;

namespace LocalList.API.NET.Shared.Startup;

public static class DatabaseServiceExtensions
{
    /// <summary>
    /// Parses the Railway/Neon PostgreSQL URL into ADO.NET format, bootstraps the pgvector
    /// extension, and registers the pooled DbContext + scoped DbContextFactory.
    /// Skips registration when no connection string is present (integration tests inject
    /// their own Postgres via ConfigureTestServices).
    /// </summary>
    public static IServiceCollection AddPostgresDatabase(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        // Parse PostgreSQL URL from Railway/Neon to standard ADO.NET format
        var connectionUrl = configuration.GetConnectionString("DefaultConnection");
        if (!string.IsNullOrEmpty(connectionUrl) && connectionUrl.StartsWith("postgres"))
        {
            var databaseUri = new Uri(connectionUrl);
            var userInfo = databaseUri.UserInfo.Split(':');
            var port = databaseUri.Port > 0 ? databaseUri.Port : 5432;
            var isInternalNetwork = databaseUri.Host.EndsWith(".railway.internal");
            var sslMode = isInternalNetwork ? "Prefer" : "Require";
            var trustCert = (environment.IsDevelopment() || isInternalNetwork) ? "Trust Server Certificate=true;" : "";
            connectionUrl = $"Host={databaseUri.Host};Port={port};Database={databaseUri.LocalPath.TrimStart('/')};Username={userInfo[0]};Password={(userInfo.Length > 1 ? userInfo[1] : "")};SslMode={sslMode};{trustCert}Maximum Pool Size=50;Minimum Pool Size=5;Connection Idle Lifetime=60;";
        }

        // Only register Npgsql when a real connection string is available.
        // Integration tests leave this empty and inject Postgres (Testcontainers) via ConfigureTestServices.
        if (!string.IsNullOrEmpty(connectionUrl))
        {
            // Bootstrap pgvector BEFORE building the pooled DataSource.
            // Npgsql caches pg_type on the first connection of each DataSource; if
            // `CREATE EXTENSION vector` runs later (inside an EF migration), the cache
            // stays stale and writes of Pgvector.Vector parameters fail with
            // "Cannot resolve 'vector' to a fully qualified datatype name."
            // Idempotent — extension already exists in prod; this covers fresh DBs.
            try
            {
                using var bootstrapConn = new NpgsqlConnection(connectionUrl);
                bootstrapConn.Open();
                using var bootstrapCmd = new NpgsqlCommand("CREATE EXTENSION IF NOT EXISTS vector;", bootstrapConn);
                bootstrapCmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[startup] pgvector bootstrap failed ({ex.GetType().Name}): {ex.Message}. Assuming extension is present and continuing.");
            }

            var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionUrl);
            dataSourceBuilder.UseVector();
            var dataSource = dataSourceBuilder.Build();

            services.AddDbContext<LocalListDbContext>(options =>
                options.UseNpgsql(dataSource, npg =>
                {
                    npg.UseVector();
                    // Cap explícito del tiempo que una query espera a DB. Sin esto, una DB colgada
                    // puede bloquear request threads hasta el timeout default (30s). 10s es el
                    // sweet-spot entre tolerancia a latencia transitoria y failure-fast.
                    npg.CommandTimeout(10);
                }));

            // Scoped factory for RouteResolver.ResolveSegmentAsync — each concurrent pre-fetch task
            // creates its own independent DbContext via the factory, avoiding EF Core's
            // "A second operation was started on this context" on the shared scoped context.
            // Scoped lifetime is correct: RouteResolver is scoped and all calls happen within one request.
            services.AddDbContextFactory<LocalListDbContext>(options =>
                options.UseNpgsql(dataSource, npg =>
                {
                    npg.UseVector();
                    npg.CommandTimeout(10);
                }), ServiceLifetime.Scoped);
        }

        return services;
    }
}
