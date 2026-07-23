using LocalList.API.NET.Features.Admin.Places;
using LocalList.API.NET.Features.Builder.Services;
using LocalList.API.NET.Features.Chat.Services;
using LocalList.API.NET.Features.Cities;
using LocalList.API.NET.Features.Import;
using LocalList.API.NET.Features.Routing;
using LocalList.API.NET.Features.Waitlist;
using LocalList.API.NET.Shared.AI;
using LocalList.API.NET.Shared.AI.Llm;
using LocalList.API.NET.Shared.AI.Services;
using LocalList.API.NET.Shared.Coverage;
using LocalList.API.NET.Shared.I18n;
using LocalList.API.NET.Shared.PostHog;
using LocalList.API.NET.Shared.Routing;
using Microsoft.Extensions.Http.Resilience;

namespace LocalList.API.NET.Shared.Startup;

public static class DomainServiceExtensions
{
    /// <summary>
    /// Registers all domain/application services: Gemini-backed AI clients (with shared
    /// resilience), routing, the LLM fallback chain, chat slot-filling, PostHog, taxonomy
    /// and supporting infrastructure.
    /// </summary>
    public static IServiceCollection AddDomainServices(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(TimeProvider.System);
        // Gemini services share the same resilience configuration — 25s total timeout,
        // 1 retry on transient network errors only (5xx errors are treated as hard failures).
        Action<HttpStandardResilienceOptions> geminiResilienceOpts = options =>
        {
            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(10);
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(25);
            options.CircuitBreaker.FailureRatio = 0.5;
            options.Retry.MaxRetryAttempts = 1;
            options.Retry.ShouldHandle = args => ValueTask.FromResult(
                args.Outcome.Exception is HttpRequestException);
        };
        services.AddHttpClient<IPlaceTranslatorService, PlaceTranslatorService>(c => c.Timeout = TimeSpan.FromSeconds(25))
            .AddStandardResilienceHandler(geminiResilienceOpts);
        services.AddHttpClient<IDescriptionGeneratorService, DescriptionGeneratorService>(c => c.Timeout = TimeSpan.FromSeconds(25))
            .AddStandardResilienceHandler(geminiResilienceOpts);

        services.AddHttpClient<EmbeddingService>(c => c.Timeout = TimeSpan.FromSeconds(15))
            .AddStandardResilienceHandler(options =>
            {
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(8);
                options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(15);
                options.Retry.MaxRetryAttempts = 1;
                options.Retry.ShouldHandle = args => ValueTask.FromResult(
                    args.Outcome.Exception is HttpRequestException);
            });

        services.AddHttpContextAccessor();
        services.AddScoped<LanguageAccessor>();
        services.AddScoped<PlaceRankingService>();
        services.AddScoped<SchedulingService>();
        services.AddScoped<PlanGenerationService>();
        services.AddScoped<IPlanGenerationService>(sp => sp.GetRequiredService<PlanGenerationService>());
        services.AddHttpClient<IRoutingService, MapboxRoutingService>(c => c.Timeout = TimeSpan.FromSeconds(8));
        services.AddHttpClient<IGooglePlacesService, GooglePlacesService>(c => c.Timeout = TimeSpan.FromSeconds(15))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddScoped<RouteResolver>();
        services.AddScoped<PlaceImportService>();
        services.AddScoped<ISegmentResolver>(sp => sp.GetRequiredService<RouteResolver>());
        services.AddHttpClient<KlaviyoService>(c => c.Timeout = TimeSpan.FromSeconds(8));
        services.AddScoped<IEmailMarketingService, KlaviyoService>();

        // LLM fallback chain (camino crítico: chat slot-filling + builder preferences).
        // Timeouts cortos por provider: con varios providers en cadena el peor caso debe
        // caber en el presupuesto de ~20s del turno de chat.
        services.Configure<LlmOptions>(configuration.GetSection(LlmOptions.SectionName));
        services.AddSingleton<LlmProviderHealthRegistry>();
        foreach (var llmProviderName in new[] { "gemini", "openai", "mistral", "anthropic" })
        {
            services.AddHttpClient($"llm-{llmProviderName}", c => c.Timeout = TimeSpan.FromSeconds(12))
                .AddStandardResilienceHandler(options =>
                {
                    options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(8);
                    options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(10);
                    options.Retry.MaxRetryAttempts = 1;
                    options.Retry.ShouldHandle = args => ValueTask.FromResult(
                        args.Outcome.Exception is HttpRequestException);
                });
        }
        services.AddScoped<ILlmClient>(LlmClientFactory.BuildChain);
        services.AddScoped<PreferenceExtractorService>();

        // Import de vídeo (F2). Servicio autocontenido: sube el vídeo a la Gemini File API,
        // extrae sitios con gemini-3.1-flash y borra el fichero tras extraer. NO participa en
        // la cadena de fallback (solo Gemini tiene el fichero). Timeouts holgados: subir 150MB
        // + transcodificado + generateContent multimodal tarda mucho más que un turno de chat.
        services.Configure<ImportOptions>(configuration.GetSection(ImportOptions.SectionName));
        services.AddHttpClient<IGeminiFileClient, GeminiFileClient>(c => c.Timeout = TimeSpan.FromSeconds(120));
        services.AddHttpClient<VideoExtractionService>(c => c.Timeout = TimeSpan.FromSeconds(120));

        // Chat — slot-filling agent
        services.AddScoped<SlotExtractorService>();
        services.AddScoped<ChatAgentService>();
        services.AddScoped<ChatSecLogger>();
        services.AddHttpClient<PostHogService>(c =>
        {
            c.BaseAddress = new Uri(configuration["PostHog:Host"] ?? "https://eu.i.posthog.com");
            c.Timeout = TimeSpan.FromSeconds(5);
        });

        // Coverage gate — allowlist de ciudades LIVE (Coverage:LiveCities). Singleton:
        // la allowlist se resuelve una vez en boot (config fija).
        services.AddSingleton<ICityCoverageService, CityCoverageService>();

        services.AddMemoryCache();
        services.AddScoped<LocalList.API.NET.Shared.Taxonomy.ITaxonomyService, LocalList.API.NET.Shared.Taxonomy.TaxonomyService>();

        return services;
    }
}
