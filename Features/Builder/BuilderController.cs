using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.Text.Json;
using LocalList.API.NET.Features.Builder.Services;
using LocalList.API.NET.Shared.Auth;
using LocalList.API.NET.Shared.Coverage;
using LocalList.API.NET.Shared.Data;
using LocalList.API.NET.Shared.Data.Entities;
using LocalList.API.NET.Shared.Dtos;
using LocalList.API.NET.Shared.I18n;
using LocalList.API.NET.Shared.Observability;
using LocalList.API.NET.Shared.PostHog;
using LocalList.API.NET.Shared.Usage;

namespace LocalList.API.NET.Features.Builder;

[ApiController]
[Route("builder")]
public class BuilderController : ControllerBase
{
    /// <summary>
    /// Presupuesto global de la generación. Con 4 providers degradados, la cadena de
    /// fallback puede tardar 4 × TotalRequestTimeout (10s) = 40s; el token enlazado
    /// corta el pipeline entero (extracción + RAG + scheduler) al vencer el presupuesto.
    /// Los providers no convierten la cancelación del caller en fallback
    /// (filtro !ct.IsCancellationRequested), así que la OperationCanceledException sube.
    /// </summary>
    private static readonly TimeSpan GenerateLlmBudget = TimeSpan.FromSeconds(30);

    private readonly LocalListDbContext _db;
    private readonly PlanGenerationService _planGen;
    private readonly SchedulingService _scheduler;
    private readonly ILogger<BuilderController> _logger;
    private readonly PostHogService _posthog;
    private readonly IPlanGenerationGateService _planGate;
    private readonly ICityCoverageService _coverage;

    public BuilderController(
        LocalListDbContext db,
        PlanGenerationService planGen,
        SchedulingService scheduler,
        ILogger<BuilderController> logger,
        PostHogService posthog,
        IPlanGenerationGateService planGate,
        ICityCoverageService coverage)
    {
        _db = db;
        _planGen = planGen;
        _scheduler = scheduler;
        _logger = logger;
        _posthog = posthog;
        _planGate = planGate;
        _coverage = coverage;
    }

    /// <summary>
    /// F4: auth requerida — sin identidad no hay contador mensual posible. El gate del
    /// catálogo Plus (PlanGenerationGateService) corre tras la validación de input y
    /// ANTES de arrancar el pipeline: valida tier fresco de DB, duración y cupo de
    /// guardados, y consume el contador (3/mes free · 50/día Plus). Una vez consumido,
    /// el permiso no se devuelve aunque el LLM falle (ver README de Billing).
    /// </summary>
    [HttpPost("chat")]
    [Authorize]
    [EnableRateLimiting("BuilderLimit")]
    public async Task<IActionResult> GeneratePlan([FromBody] BuilderChatRequest request, CancellationToken ct)
    {
        Guid? userId = await User.GetUserIdAsync(_db, ct);
        if (userId == null)
            return Unauthorized(new { error = "Invalid token claims." });

        _logger.LogInformation(
            "Builder: request userId={UserId} city={City} days={Days} groupType={GroupType} categories={Categories} budget={Budget} msgLen={MsgLen}",
            userId,
            request.TripContext?.City ?? "(unset)",
            request.TripContext?.Days?.ToString() ?? "(unset)",
            request.TripContext?.GroupType ?? "(unset)",
            request.TripContext?.Categories == null ? "(null)" : string.Join(",", request.TripContext.Categories),
            request.TripContext?.Budget ?? "(unset)",
            request.Message?.Length ?? 0);

        var inputCheck = ValidateMinimumInput(request);
        if (!inputCheck.accepted)
        {
            _logger.LogInformation(
                "Builder: rejected insufficient_input — hasMsg={M} wizardCity={C} wizardDays={D} wizardGroup={G} wizardInterests={I} wizardBudget={B}",
                inputCheck.signals["chat_message"],
                inputCheck.signals["wizard_city"],
                inputCheck.signals["wizard_days"],
                inputCheck.signals["wizard_groupType"],
                inputCheck.signals["wizard_interests"],
                inputCheck.signals["wizard_budget"]);
            return BadRequest(new
            {
                error = "insufficient_input",
                message = "Please complete at least 3 wizard steps (city, duration, group, interests, budget).",
                signals = inputCheck.signals
            });
        }

        // Defensa de cobertura ANTES del gate (m1/F4): espeja /chat/generate. Sin este check
        // una ciudad no cubierta (retrieval 0 places → soft-fallback 200) consumía un permiso
        // del contador mensual sin producir un plan real. El contador solo debe gastarse si la
        // generación puede arrancar de verdad, así que rechazamos aquí sin consumir.
        var city = request.TripContext?.City;
        if (!_coverage.IsLive(city))
        {
            _logger.LogInformation("Builder: blocked, city not covered city={City}", city ?? "(null)");
            return BadRequest(new
            {
                error = "city_unsupported",
                city,
                liveCities = _coverage.LiveCities,
            });
        }

        // Gate del catálogo Plus — último check antes de arrancar (y pagar) el pipeline.
        var gate = await _planGate.CheckAndConsumeAsync(userId.Value, request.TripContext?.Days, ct);
        if (!gate.Allowed)
            return StatusCode(gate.Rejection!.StatusCode, gate.Rejection.Body);

        using var llmBudget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        llmBudget.CancelAfter(GenerateLlmBudget);

        try
        {
            var lang = LanguageAccessor.ResolveRequestLanguage(Request);
            var result = await _planGen.GenerateAsync(request.Message, request.TripContext, lang, gate.MaxDays, llmBudget.Token);

            if (result == null)
            {
                // Soft fallback: no places found — return an empty plan rather than 404 so the
                // wizard doesn't surface an error to the user. (No IsEphemeral: los planes
                // efímeros ya no existen en el contrato — era un resto muerto.)
                var fallbackCity = request.TripContext?.City ?? "Miami";
                return Ok(new
                {
                    plan = new
                    {
                        Id = Guid.NewGuid(),
                        Name = $"Plan for {fallbackCity}",
                        City = fallbackCity,
                        Type = "ai",
                        Description = "No places found matching your preferences in this city yet.",
                        DurationDays = request.TripContext?.Days ?? 1,
                        TripContext = request.TripContext,
                        IsPublic = false
                    },
                    stops = Array.Empty<object>(),
                    message = "No places found for this city yet.",
                    warnings = new[] { "no_places_available" },
                    appliedRefinements = Array.Empty<string>()
                });
            }

            var planStopsData = result.Schedule.Stops;

            var plan = new Plan
            {
                Name = result.PlanName,
                City = result.City,
                Type = "ai",
                Description = result.PlanDescription,
                DurationDays = result.Prefs.Days,
                TripContext = request.TripContext != null
                    ? JsonSerializer.SerializeToDocument(request.TripContext)
                    : JsonSerializer.SerializeToDocument(new {}),
                IsPublic = false,
                CreatedById = userId,
                NameI18n = LanguageAccessor.SetI18nString(null, lang, result.PlanName),
                DescriptionI18n = LanguageAccessor.SetI18nString(null, lang, result.PlanDescription),
            };

            _db.Plans.Add(plan);

            var stopsToInsert = planStopsData.Select(sd => new PlanStop
            {
                PlanId = plan.Id,
                PlaceId = sd.PlaceId,
                DayNumber = sd.DayNumber,
                OrderIndex = sd.OrderIndex,
                TimeBlock = sd.TimeBlock,
                SuggestedArrival = string.IsNullOrEmpty(sd.SuggestedArrival) ? null : TimeSpan.Parse(sd.SuggestedArrival),
                SuggestedDurationMin = sd.SuggestedDurationMin,
                TravelFromPrevious = sd.TravelFromPrevious != null ? JsonSerializer.SerializeToDocument(sd.TravelFromPrevious) : null
            }).ToList();

            if (stopsToInsert.Any())
                _db.PlanStops.AddRange(stopsToInsert);

            var filledSignals = (short)(
                (request.TripContext?.City != null ? 1 : 0) +
                (request.TripContext?.Days.HasValue == true ? 1 : 0) +
                (request.TripContext?.GroupType != null ? 1 : 0) +
                ((request.TripContext?.Categories?.Count > 0 || request.TripContext?.Subcategories?.Count > 0) ? 1 : 0) +
                (request.TripContext?.Budget != null ? 1 : 0));

            _db.PlanMetrics.Add(new PlanMetric
            {
                PlanId = plan.Id,
                GenerationSource = "builder",
                SignalsFilled = filledSignals,
                NumDays = result.Prefs.Days,
                NumStops = planStopsData.Count,
                NumCategories = result.Prefs.Categories?.Count ?? 0,
                GroupType = result.Prefs.GroupType,
                Budget = request.TripContext?.Budget,
                AiProvider = result.LlmDiagnostics?.Provider,
                LatencyMs = result.LlmDiagnostics?.LatencyMs ?? 0,
                CostUsd = result.LlmDiagnostics?.CostUsd,
            });

            await _db.SaveChangesAsync(ct);

            _ = _posthog.CaptureAsync(userId!.Value.ToString(), "plan_generated", new()
            {
                ["plan_id"] = plan.Id.ToString(),
                ["city"] = result.City,
                ["days"] = result.Prefs.Days,
                ["stops"] = planStopsData.Count,
                ["source"] = "builder",
            });

            return Ok(new
            {
                plan,
                stops = _scheduler.ResolveStopPlaces(planStopsData, result.FilteredPlaces),
                message = $"Created a {result.Prefs.Days}-day plan with {planStopsData.Count} stops!",
                warnings = result.Schedule.Warnings,
                appliedRefinements = result.Schedule.AppliedRefinements,
                clamped = DaysClampInfo.ToHint(result.DaysClamp, gate.MaxDays),
            });
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogError("Builder: generation exceeded global LLM budget ({Budget}s)", GenerateLlmBudget.TotalSeconds);
            return StatusCode(504, new { error = "ai_timeout" });
        }
        catch (OperationCanceledException)
        {
            return StatusCode(499, new { error = "Request cancelled" });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Gemini API call failed during plan generation");
            return StatusCode(502, new { error = "AI service temporarily unavailable" });
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            _logger.LogError(ex, "Plan generation failed");
            return StatusCode(500, new { error = "Failed to generate plan" });
        }
    }

    // ── Plan naming — test contract surface (BuilderPlanNameTests.cs calls these directly) ──

    public static string BuildPlanName(ExtractedPreferences prefs, string city, string rawMessage)
        => PlanNamingService.BuildPlanName(prefs, city, rawMessage);

    public static string BuildPlanDescription(ExtractedPreferences prefs)
        => PlanNamingService.BuildPlanDescription(prefs);

    // ── Input validation ──────────────────────────────────────────────────────────

    private static (bool accepted, Dictionary<string, bool> signals) ValidateMinimumInput(BuilderChatRequest request)
    {
        var hasMessage = !string.IsNullOrWhiteSpace(request.Message);

        var city      = !string.IsNullOrWhiteSpace(request.TripContext?.City);
        var days      = request.TripContext?.Days.HasValue == true;
        var groupType = !string.IsNullOrWhiteSpace(request.TripContext?.GroupType);
        var interests = (request.TripContext?.Categories?.Count > 0)
                     || (request.TripContext?.Subcategories?.Count > 0);
        var budget    = !string.IsNullOrWhiteSpace(request.TripContext?.Budget);

        var wizardSignals = (city ? 1 : 0)
                          + (days ? 1 : 0)
                          + (groupType ? 1 : 0)
                          + (interests ? 1 : 0)
                          + (budget ? 1 : 0);

        var accepted = wizardSignals >= 3;

        return (accepted, new Dictionary<string, bool>
        {
            ["chat_message"]     = hasMessage,
            ["wizard_city"]      = city,
            ["wizard_days"]      = days,
            ["wizard_groupType"] = groupType,
            ["wizard_interests"] = interests,
            ["wizard_budget"]    = budget,
        });
    }
}
