using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using LocalList.API.NET.Shared.AI.Services;
using LocalList.API.NET.Shared.Coverage;
using LocalList.API.NET.Shared.Dtos;
using LocalList.API.NET.Features.Chat.I18n;
using LocalList.API.NET.Features.Chat.Services;
using LocalList.API.NET.Shared.Auth;
using LocalList.API.NET.Shared.Data;
using LocalList.API.NET.Shared.Data.Entities;
using LocalList.API.NET.Shared.I18n;
using LocalList.API.NET.Shared.Observability;
using LocalList.API.NET.Shared.PostHog;

namespace LocalList.API.NET.Features.Chat;

[ApiController]
[Route("chat")]
public class ChatController : ControllerBase
{
    /// <summary>
    /// Presupuesto global del turno de chat. Con 4 providers degradados, la cadena de
    /// fallback puede tardar 4 × TotalRequestTimeout (10s) = 40s; el token enlazado
    /// corta la cadena entera al vencer el presupuesto. Los providers no convierten la
    /// cancelación del caller en fallback (filtro !ct.IsCancellationRequested), así que
    /// la OperationCanceledException sube hasta aquí.
    /// </summary>
    private static readonly TimeSpan TurnLlmBudget = TimeSpan.FromSeconds(25);

    /// <summary>Presupuesto de /chat/generate (pipeline completo: extracción + RAG + scheduler).</summary>
    private static readonly TimeSpan GenerateLlmBudget = TimeSpan.FromSeconds(30);

    private readonly ChatAgentService _agent;
    private readonly LocalListDbContext _db;
    private readonly IPlanGenerationService _planGen;
    private readonly ILogger<ChatController> _logger;
    private readonly PostHogService _posthog;
    private readonly ICityCoverageService _coverage;

    public ChatController(
        ChatAgentService agent,
        LocalListDbContext db,
        IPlanGenerationService planGen,
        ILogger<ChatController> logger,
        PostHogService posthog,
        ICityCoverageService coverage)
    {
        _agent = agent;
        _db = db;
        _planGen = planGen;
        _logger = logger;
        _posthog = posthog;
        _coverage = coverage;
    }

    /// <summary>
    /// Process one chat turn. Returns updated slots, AI message, and quick-reply chips.
    /// Anonymous users are supported (plan is ephemeral). Authenticated users get sessions
    /// persisted and tied to their account.
    ///
    /// Rate limiting (3 capas reales encadenadas, ver RateLimitingExtensions):
    ///   - GlobalLimiter burst: 100/min por IP (todos los endpoints).
    ///   - Techo por IP anti-farming: 120/hr por IP (namespace chatturn_ip_ceiling); ninguna
    ///     IP lo supera por más cuentas que registre.
    ///   - ChatTurnLimit (esta política): sliding window 20/hr anónimo por IP · 40/hr
    ///     autenticado por userId — bucket alto SOLO para tokens AppScheme (un token
    ///     Firebase/Anonymous cae al bucket por IP).
    /// </summary>
    [HttpPost("turn")]
    [AllowAnonymous]
    [EnableRateLimiting("ChatTurnLimit")]  // sliding-window hourly; burst covered by global 100/min
    public async Task<IActionResult> Turn([FromBody] ChatTurnRequest request, CancellationToken ct)
    {
        var hasContent = !string.IsNullOrWhiteSpace(request.Message) || !string.IsNullOrWhiteSpace(request.QuickReplyId);
        var hasPreSeed = request.SessionId == null && !string.IsNullOrWhiteSpace(request.PreSeededSlots?.City);
        if (request == null || (!hasContent && !hasPreSeed))
            return BadRequest(new { error = "message or quickReplyId is required" });

        // Validate quickReplyId length to prevent oversized forged chip IDs
        if (!string.IsNullOrWhiteSpace(request.QuickReplyId) && request.QuickReplyId.Length > 50)
            return BadRequest(new { error = "quickReplyId too long" });

        var isAnonymous = !User.Identity?.IsAuthenticated ?? true;
        Guid? userId = isAnonymous ? null : await User.GetUserIdAsync(_db, ct);
        var rawIp = HttpContext.Connection.RemoteIpAddress?.ToString();
        var lang = LanguageAccessor.ResolveRequestLanguage(Request);

        using var llmBudget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        llmBudget.CancelAfter(TurnLlmBudget);

        ChatTurnResponse response;
        try
        {
            response = await _agent.ProcessTurnAsync(
                request.SessionId,
                request.Message,
                request.QuickReplyId,
                request.PreSeededSlots,
                userId,
                rawIp,
                lang,
                llmBudget.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogError("Chat: turn exceeded global LLM budget ({Budget}s)", TurnLlmBudget.TotalSeconds);
            return StatusCode(504, new { error = "ai_timeout" });
        }

        if (response.Quarantined)
            return StatusCode(403, response);

        return Ok(response);
    }

    /// <summary>
    /// Generates a plan from a ready chat session. Idempotent: calling again returns the same plan.
    /// Requires the session to be in "ready" status (all Tier 1 slots filled) or at turn cap.
    /// Rate-limited alongside /builder/chat (BuilderLimit).
    /// </summary>
    [HttpPost("generate")]
    [AllowAnonymous]
    [EnableRateLimiting("BuilderLimit")]
    public async Task<IActionResult> Generate([FromBody] ChatGenerateRequest request, CancellationToken ct)
    {
        var isAnonymous = !User.Identity?.IsAuthenticated ?? true;
        Guid? userId = isAnonymous ? null : await User.GetUserIdAsync(_db, ct);
        var rawIp = HttpContext.Connection.RemoteIpAddress?.ToString();

        var (session, error) = await _agent.FindSessionForGenerationAsync(request.SessionId, userId, rawIp, ct);

        if (session == null)
        {
            return error switch
            {
                "session_not_found" => NotFound(new { error }),
                "session_quarantined" => StatusCode(403, new { error }),
                _ => StatusCode(403, new { error })
            };
        }

        // Session must be ready (or at turn cap)
        if (session.Status is not ("ready" or "generated") && session.TurnCount < 6)
            return BadRequest(new { error = "session_not_ready", turnCount = session.TurnCount });

        // Idempotency: already generated → return the existing plan
        if (session.Status == "generated" && session.GeneratedPlanId.HasValue)
        {
            var existing = await _db.Plans
                .Include(p => p.Stops).ThenInclude(s => s.Place)
                .FirstOrDefaultAsync(p => p.Id == session.GeneratedPlanId.Value, ct);
            if (existing != null)
            {
                _logger.LogInformation("Chat: generate idempotent planId={Plan} sessionId={Session}",
                    existing.Id, session.Id);
                var existingStopDtos = existing.Stops
                    .OrderBy(s => s.DayNumber).ThenBy(s => s.OrderIndex)
                    .Select(s => new ScheduledStopDto
                    {
                        PlaceId = s.PlaceId,
                        DayNumber = s.DayNumber,
                        OrderIndex = s.OrderIndex,
                        TimeBlock = s.TimeBlock ?? "",
                        SuggestedArrival = s.SuggestedArrival?.ToString(),
                        SuggestedDurationMin = s.SuggestedDurationMin ?? 0
                    }).ToList();
                var existingPlaces = existing.Stops.Select(s => s.Place).OfType<Place>().ToList();
                return Ok(new
                {
                    plan = existing,
                    stops = _planGen.ResolveStopPlaces(existingStopDtos, existingPlaces),
                    message = "Your plan is ready!",
                    warnings = Array.Empty<string>(),
                    appliedRefinements = Array.Empty<string>(),
                    isExisting = true
                });
            }
        }

        // Build TripContextDto + summary message from slots
        var slots = ChatAgentService.GetSlots(session);
        var tripContext = ChatAgentService.SlotsToTripContext(slots);
        var summaryMessage = ChatAgentService.BuildSummaryMessage(slots);
        var lang = LanguageAccessor.ResolveRequestLanguage(Request);

        // Defensa de cobertura: /chat/turn ya debería haber bloqueado las ciudades
        // no cubiertas, pero si una sesión llega aquí con una (prefill antiguo, etc.)
        // respondemos estructurado y amable en vez de un 404 seco de "no places".
        if (!_coverage.IsLive(slots.City))
        {
            _logger.LogInformation("Chat: generate blocked, city not covered sessionId={Session} city={City}",
                session.Id, slots.City ?? "(null)");
            return BadRequest(new
            {
                error = "city_unsupported",
                message = ChatStrings.CityUnsupported(lang, slots.City ?? string.Empty, _coverage.LiveCities),
                city = slots.City,
                liveCities = _coverage.LiveCities,
            });
        }

        _logger.LogInformation(
            "Chat: generate sessionId={Session} city={City} days={Days} summary='{Summary}'",
            session.Id, tripContext.City ?? "(null)", tripContext.Days, summaryMessage);

        using var llmBudget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        llmBudget.CancelAfter(GenerateLlmBudget);

        PlanGenerationResult? result;
        try
        {
            result = await _planGen.GenerateAsync(summaryMessage, tripContext, lang, llmBudget.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogError("Chat: generate exceeded global LLM budget ({Budget}s)", GenerateLlmBudget.TotalSeconds);
            return StatusCode(504, new { error = "ai_timeout" });
        }

        if (result == null)
            return NotFound(new { error = "no_places_available", message = "No places found for this city yet." });

        // Anonymous → ephemeral plan, still mark session generated
        if (isAnonymous)
        {
            if (result.LlmDiagnostics != null)
            {
                _db.ChatTurns.Add(new ChatTurn
                {
                    SessionId = session.Id,
                    TurnIndex = session.TurnCount,
                    AiProvider = result.LlmDiagnostics.Provider,
                    Model = result.LlmDiagnostics.Model,
                    PromptVersion = "slot-v1",
                    ContextSignalsJson = session.SlotsJson,
                    PromptChars = result.LlmDiagnostics.Prompt.Length,
                    PromptExcerpt = PiiRedactor.Redact(
                        result.LlmDiagnostics.Prompt.Length > 500
                            ? result.LlmDiagnostics.Prompt[..500]
                            : result.LlmDiagnostics.Prompt),
                    ResponseRaw = result.LlmDiagnostics.ResponseRaw != null
                        ? PiiRedactor.Redact(result.LlmDiagnostics.ResponseRaw) : null,
                    FinishReason = result.LlmDiagnostics.FinishReason,
                    LatencyMs = result.LlmDiagnostics.LatencyMs,
                    InputTokens = result.LlmDiagnostics.InputTokens,
                    OutputTokens = result.LlmDiagnostics.OutputTokens,
                    ThinkingTokens = result.LlmDiagnostics.ThinkingTokens,
                    TotalTokens = result.LlmDiagnostics.TotalTokens,
                    CostUsd = result.LlmDiagnostics.CostUsd,
                    GeminiStatus = result.LlmDiagnostics.HttpStatus,
                    ErrorCode = result.LlmDiagnostics.ErrorCode,
                    ErrorMessage = result.LlmDiagnostics.ErrorMessage,
                });
            }

            session.Status = "generated";
            _db.Update(session);
            await _db.SaveChangesAsync(ct);

            var ephemeralPlan = new
            {
                Id = Guid.NewGuid(),
                Name = result.PlanName,
                City = result.City,
                Type = "ai",
                Description = result.PlanDescription,
                DurationDays = result.Prefs.Days,
                IsPublic = false,
                IsEphemeral = true
            };

            return Ok(new
            {
                plan = ephemeralPlan,
                stops = _planGen.ResolveStopPlaces(result.Schedule.Stops, result.FilteredPlaces),
                message = $"Created a {result.Prefs.Days}-day plan with {result.Schedule.Stops.Count} stops!",
                warnings = result.Schedule.Warnings,
                appliedRefinements = result.Schedule.AppliedRefinements
            });
        }

        // Authenticated → persist plan + update session
        var plan = new Plan
        {
            Name = result.PlanName,
            City = result.City,
            Type = "ai",
            Description = result.PlanDescription,
            DurationDays = result.Prefs.Days,
            TripContext = JsonSerializer.SerializeToDocument(tripContext),
            IsPublic = false,
            CreatedById = userId,
            NameI18n = LanguageAccessor.SetI18nString(null, lang, result.PlanName),
            DescriptionI18n = LanguageAccessor.SetI18nString(null, lang, result.PlanDescription),
        };
        _db.Plans.Add(plan);

        var stopsToInsert = result.Schedule.Stops.Select(sd => new PlanStop
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

        var generateTurn = result.LlmDiagnostics != null ? new ChatTurn
        {
            SessionId = session.Id,
            UserId = userId,
            TurnIndex = session.TurnCount,
            AiProvider = result.LlmDiagnostics.Provider,
            Model = result.LlmDiagnostics.Model,
            PromptVersion = "slot-v1",
            ContextSignalsJson = session.SlotsJson,
            PromptChars = result.LlmDiagnostics.Prompt.Length,
            PromptExcerpt = PiiRedactor.Redact(
                result.LlmDiagnostics.Prompt.Length > 500
                    ? result.LlmDiagnostics.Prompt[..500]
                    : result.LlmDiagnostics.Prompt),
            ResponseRaw = result.LlmDiagnostics.ResponseRaw != null
                ? PiiRedactor.Redact(result.LlmDiagnostics.ResponseRaw) : null,
            FinishReason = result.LlmDiagnostics.FinishReason,
            LatencyMs = result.LlmDiagnostics.LatencyMs,
            InputTokens = result.LlmDiagnostics.InputTokens,
            OutputTokens = result.LlmDiagnostics.OutputTokens,
            ThinkingTokens = result.LlmDiagnostics.ThinkingTokens,
            TotalTokens = result.LlmDiagnostics.TotalTokens,
            CostUsd = result.LlmDiagnostics.CostUsd,
            GeminiStatus = result.LlmDiagnostics.HttpStatus,
            ErrorCode = result.LlmDiagnostics.ErrorCode,
            ErrorMessage = result.LlmDiagnostics.ErrorMessage,
        } : null;

        if (generateTurn != null) _db.ChatTurns.Add(generateTurn);

        _db.PlanMetrics.Add(new PlanMetric
        {
            PlanId = plan.Id,
            ChatSessionId = session.Id,
            GenerateTurnId = generateTurn?.Id,
            GenerationSource = "chat",
            SignalsFilled = CountFilledSlots(slots),
            NumDays = result.Prefs.Days,
            NumStops = stopsToInsert.Count,
            NumCategories = result.Prefs.Categories.Count,
            GroupType = result.Prefs.GroupType,
            Budget = slots.Budget,
            VibesJson = result.Prefs.Vibes.Count > 0
                ? System.Text.Json.JsonSerializer.Serialize(result.Prefs.Vibes) : null,
            PromptVersion = "slot-v1",
            AiProvider = result.LlmDiagnostics?.Provider,
            LatencyMs = result.LlmDiagnostics?.LatencyMs ?? 0,
            CostUsd = result.LlmDiagnostics?.CostUsd,
        });

        session.Status = "generated";
        session.GeneratedPlanId = plan.Id;
        _db.Update(session);

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Chat: generated planId={Plan} stops={N} sessionId={Session}",
            plan.Id, stopsToInsert.Count, session.Id);

        _ = _posthog.CaptureAsync(userId!.Value.ToString(), "plan_generated", new()
        {
            ["plan_id"] = plan.Id.ToString(),
            ["city"] = result.City,
            ["days"] = result.Prefs.Days,
            ["stops"] = stopsToInsert.Count,
            ["source"] = "chat",
            ["session_id"] = session.Id.ToString(),
        });

        return Ok(new
        {
            plan,
            stops = _planGen.ResolveStopPlaces(result.Schedule.Stops, result.FilteredPlaces),
            message = $"Created a {result.Prefs.Days}-day plan with {result.Schedule.Stops.Count} stops!",
            warnings = result.Schedule.Warnings,
            appliedRefinements = result.Schedule.AppliedRefinements
        });
    }

    private static short CountFilledSlots(ChatSlots slots)
    {
        short count = 0;
        if (!string.IsNullOrWhiteSpace(slots.City)) count++;
        if (slots.Days.HasValue) count++;
        if (!string.IsNullOrWhiteSpace(slots.GroupType)) count++;
        if (slots.Categories.Count > 0) count++;
        if (!string.IsNullOrWhiteSpace(slots.Budget)) count++;
        return count;
    }

    /// <summary>
    /// Delete a chat session (GDPR on-demand erasure).
    /// </summary>
    [HttpDelete("session/{id:guid}")]
    [Authorize]
    public async Task<IActionResult> DeleteSession(Guid id, CancellationToken ct)
    {
        var userId = await User.GetUserIdAsync(_db, ct);
        if (userId == null) return Unauthorized();

        var session = await _db.ChatSessions
            .FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId, ct);

        if (session == null) return NotFound();

        _db.ChatSessions.Remove(session);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Chat: session deleted id={Id} userId={UserId}", id, userId);
        return NoContent();
    }
}
