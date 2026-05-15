using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using LocalList.API.NET.Features.Builder.Services;
using LocalList.API.NET.Features.Chat.Services;
using LocalList.API.NET.Shared.Auth;
using LocalList.API.NET.Shared.Data;
using LocalList.API.NET.Shared.Data.Entities;
using LocalList.API.NET.Shared.I18n;
using LocalList.API.NET.Shared.PostHog;

namespace LocalList.API.NET.Features.Chat;

[ApiController]
[Route("chat")]
public class ChatController : ControllerBase
{
    private readonly ChatAgentService _agent;
    private readonly LocalListDbContext _db;
    private readonly PlanGenerationService _planGen;
    private readonly SchedulingService _scheduler;
    private readonly ILogger<ChatController> _logger;
    private readonly PostHogService _posthog;

    public ChatController(
        ChatAgentService agent,
        LocalListDbContext db,
        PlanGenerationService planGen,
        SchedulingService scheduler,
        ILogger<ChatController> logger,
        PostHogService posthog)
    {
        _agent = agent;
        _db = db;
        _planGen = planGen;
        _scheduler = scheduler;
        _logger = logger;
        _posthog = posthog;
    }

    /// <summary>
    /// Process one chat turn. Returns updated slots, AI message, and quick-reply chips.
    /// Anonymous users are supported (plan is ephemeral). Authenticated users get sessions
    /// persisted and tied to their account.
    ///
    /// Rate limiting (4 layers):
    ///   - ChatBurst: 5 req / 10s per IP (token-bucket)
    ///   - ChatTurnLimit: 20/hr anon, 40/hr auth (sliding window)
    ///   - ChatNewSession: 5 new sessions / hr / IP (applied conditionally below)
    ///   - ChatDaily: 200 turns / 24h / IP
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

        var response = await _agent.ProcessTurnAsync(
            request.SessionId,
            request.Message,
            request.QuickReplyId,
            request.PreSeededSlots,
            userId,
            rawIp,
            ct);

        // 403 = quarantined session
        if (response.AiMessage.Contains("reset for safety", StringComparison.OrdinalIgnoreCase))
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
                    stops = _scheduler.ResolveStopPlaces(existingStopDtos, existingPlaces),
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

        _logger.LogInformation(
            "Chat: generate sessionId={Session} city={City} days={Days} summary='{Summary}'",
            session.Id, tripContext.City ?? "(null)", tripContext.Days, summaryMessage);

        var result = await _planGen.GenerateAsync(summaryMessage, tripContext, lang, ct);

        if (result == null)
            return NotFound(new { error = "no_places_available", message = "No places found for this city yet." });

        // Anonymous → ephemeral plan, still mark session generated
        if (isAnonymous)
        {
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
                stops = _scheduler.ResolveStopPlaces(result.Schedule.Stops, result.FilteredPlaces),
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
            stops = _scheduler.ResolveStopPlaces(result.Schedule.Stops, result.FilteredPlaces),
            message = $"Created a {result.Prefs.Days}-day plan with {result.Schedule.Stops.Count} stops!",
            warnings = result.Schedule.Warnings,
            appliedRefinements = result.Schedule.AppliedRefinements
        });
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
