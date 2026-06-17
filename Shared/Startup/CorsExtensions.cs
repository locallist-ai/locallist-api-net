namespace LocalList.API.NET.Shared.Startup;

public static class CorsExtensions
{
    /// <summary>
    /// Registers the "AllowSpecificOrigins" CORS policy. Defaults to locallist.ai in
    /// production and localhost dev servers otherwise; Cors:AllowedOrigins
    /// (env Cors__AllowedOrigins, ';'-separated) extends the defaults without a code redeploy.
    /// </summary>
    public static IServiceCollection AddCorsPolicy(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddCors(options =>
        {
            options.AddPolicy("AllowSpecificOrigins", corsBuilder =>
            {
                var defaultOrigins = environment.IsProduction()
                    ? new[] { "https://locallist.ai" }
                    : new[] { "http://localhost:8081", "http://localhost:19006" };

                // Cors:AllowedOrigins (env: Cors__AllowedOrigins, separados por ';') amplía los
                // defaults sin redeploy de código — p. ej. la admin web en localhost contra prod.
                var extraOrigins = configuration["Cors:AllowedOrigins"]
                    ?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    ?? Array.Empty<string>();

                corsBuilder.WithOrigins(defaultOrigins.Concat(extraOrigins).Distinct().ToArray())
                    .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS")
                    .WithHeaders("Content-Type", "Authorization")
                    .AllowCredentials();
            });
        });

        return services;
    }
}
