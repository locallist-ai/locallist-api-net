using System.Text.Json;
using LocalList.API.NET.Shared.Coverage;
using LocalList.API.NET.Features.Chat.I18n;
using LocalList.API.NET.Shared.Data;
using LocalList.API.NET.Shared.Data.Entities;
using LocalList.API.NET.Shared.Observability;
using LocalList.API.NET.Shared.PostHog;
using Microsoft.EntityFrameworkCore;

namespace LocalList.API.NET.Features.Chat.Services;

// ChatAgentService is split across several partial files by responsibility. This split is
// purely structural (same class, same members, same behavior); it does NOT change any logic:
//   • ChatAgentService.cs            — construction + ProcessTurnAsync turn orchestration / slot-filling
//   • ChatAgentService.Constants.cs  — turn/session caps, quick-reply → slot map, universal-chip allowlist
//   • ChatAgentService.Responses.cs  — response building + persistence + quarantine transitions
//   • ChatAgentService.Session.cs    — load/create session, archival, profile slot pre-fill
//   • ChatAgentService.Slots.cs      — slot merge/completeness + next-question routing
//   • ChatAgentService.Generation.cs — generate-flow helpers used by ChatController.Generate
//   • ChatAgentService.Helpers.cs    — IP hashing + tolerant JSON deserialization of persisted blobs
public partial class ChatAgentService
{
    private readonly LocalListDbContext _db;
    private readonly SlotExtractorService _extractor;
    private readonly ILogger<ChatAgentService> _logger;
    private readonly ChatSecLogger _secLog;
    private readonly IConfiguration _config;
    private readonly PostHogService _posthog;
    private readonly ICityCoverageService _coverage;

    public ChatAgentService(
        LocalListDbContext db,
        SlotExtractorService extractor,
        ILogger<ChatAgentService> logger,
        ChatSecLogger secLog,
        IConfiguration config,
        PostHogService posthog,
        ICityCoverageService coverage)
    {
        _db = db;
        _extractor = extractor;
        _logger = logger;
        _secLog = secLog;
        _config = config;
        _posthog = posthog;
        _coverage = coverage;
    }

    public async Task<ChatTurnResponse> ProcessTurnAsync(
        Guid? sessionId,
        string? rawMessage,
        string? quickReplyId,
        PreSeededSlots? preSeededSlots,
        Guid? userId,
        string? rawIp,
        string lang = "en",
        CancellationToken ct = default)
    {
        var ipHash = HashIp(rawIp);
        var session = await LoadOrCreateSessionAsync(sessionId, userId, ipHash, ct);
        var suspicion = DeserializeSuspicion(session.SuspicionJson);

        // If already quarantined, refuse immediately
        if (session.Status == "quarantined")
            return BuildQuarantineResponse(session, DeserializeSlots(session.SlotsJson), lang);

        var slots = DeserializeSlots(session.SlotsJson);
        var history = DeserializeHistory(session.HistoryJson);

        // ── PreSeededSlots: honor client-supplied city on brand-new sessions only ──
        if (session.TurnCount == 0 && !string.IsNullOrWhiteSpace(preSeededSlots?.City))
        {
            // Coverage gate: una ciudad de TEST puede existir en la tabla Cities con
            // places y aun así NO estar cubierta. La allowlist manda sobre la DB.
            if (!_coverage.IsLive(preSeededSlots.City))
            {
                _secLog.CityNotWhitelisted(session.Id, preSeededSlots.City);
                return await BuildCityUnsupportedResponseAsync(
                    session, slots, history, suspicion, preSeededSlots.City, lang, ct);
            }

            var normalizedCity = preSeededSlots.City.Trim().ToLowerInvariant();
            var cityExists = await _db.Cities.AnyAsync(c => c.NormalizedName == normalizedCity, ct);
            if (cityExists && string.IsNullOrEmpty(slots.City))
            {
                slots.City = preSeededSlots.City.Trim();
                _logger.LogInformation("Chat: preSeeded city={City} sessionId={Session}", slots.City, session.Id);
            }

            // If client sent no message (city-only first turn), return greeting immediately
            if (string.IsNullOrWhiteSpace(rawMessage) && string.IsNullOrWhiteSpace(quickReplyId))
            {
                var greeting = string.IsNullOrEmpty(slots.City)
                    ? ChatStrings.GreetingNoCity(lang)
                    : ChatStrings.GreetingWithCity(lang, slots.City);
                var greetingReplies = ChatStrings.GreetingDayChips(lang);
                history.Add(new HistoryEntry { Role = "assistant", Content = greeting, Timestamp = DateTimeOffset.UtcNow });
                return await BuildResponseAsync(session, slots, history, suspicion, greeting, lang, ct,
                    geminiQuickReplies: greetingReplies);
            }
        }

        // ── Quick reply: deterministic slot update, no Gemini call ──
        if (!string.IsNullOrWhiteSpace(quickReplyId))
        {
            // L3 anti-forgery: chip must have been offered last turn (or be universal)
            if (!UniversalChips.Contains(quickReplyId) &&
                !session.LastOfferedChips.Contains(quickReplyId, StringComparer.OrdinalIgnoreCase))
            {
                _secLog.ChipForgeryAttempt(session.Id, quickReplyId);
                return new ChatTurnResponse
                {
                    SessionId = session.Id,
                    AiMessage = ChatStrings.ChipForgeryReject(lang),
                    Slots = slots,
                    MissingCritical = GetMissingCritical(slots),
                    QuickReplies = new(),
                    Ready = false,
                    TurnCount = session.TurnCount,
                    TurnLimit = TurnLimit
                };
            }

            if (QuickReplyMap.TryGetValue(quickReplyId, out var chipSlots))
            {
                MergeSlots(slots, chipSlots, acknowledge: false, lang, out _);
                _logger.LogInformation("Chat: quickReply={Id} sessionId={Session}", quickReplyId, session.Id);
                history.Add(new HistoryEntry
                {
                    Role = "user",
                    Content = $"[chip: {quickReplyId}]",
                    Timestamp = DateTimeOffset.UtcNow
                });
                suspicion.RecordCleanTurn();
                return await BuildResponseAsync(session, slots, history, suspicion, aiMessage: null, lang, ct);
            }
        }

        // ── Free text ──
        // L2: normalize for detection (full input, no cap — we need to see all of it)
        var messageForDetection = InputNormalizer.NormalizeForDetection(rawMessage ?? string.Empty);
        // L2: normalize for Gemini feed (capped at 500 chars)
        var message = InputNormalizer.Normalize(rawMessage ?? string.Empty);

        // L3: injection check against full normalized input (not capped)
        if (JailbreakPatternLibrary.IsInjection(messageForDetection))
        {
            suspicion.RecordInjection("pattern_match");
            _secLog.InjectionDetected(session.Id, suspicion.LastTrigger ?? "unknown", suspicion.Score);

            if (suspicion.ShouldQuarantine)
                return await QuarantineSessionAsync(session, slots, suspicion, lang, ct, "injection");

            session.SuspicionJson = JsonSerializer.Serialize(suspicion);
            _db.Update(session);
            await _db.SaveChangesAsync(ct);

            return BuildCannedResponse(session, slots,
                ChatStrings.InjectionRedirect(lang),
                nextSlot: MostUrgentMissing(slots), lang);
        }

        if (JailbreakPatternLibrary.IsOffTopic(messageForDetection))
        {
            suspicion.RecordOffTopic();
            _secLog.OffTopicDetected(session.Id, suspicion.Score);

            session.SuspicionJson = JsonSerializer.Serialize(suspicion);
            _db.Update(session);
            await _db.SaveChangesAsync(ct);

            return BuildCannedResponse(session, slots,
                ChatStrings.OffTopicRedirect(lang, NextSlotQuestion(slots, lang)),
                nextSlot: MostUrgentMissing(slots), lang);
        }

        history.Add(new HistoryEntry { Role = "user", Content = message, Timestamp = DateTimeOffset.UtcNow });

        // Turn cap
        if (session.TurnCount >= TurnLimit - 1)
        {
            _logger.LogInformation("Chat: turn cap reached sessionId={Session}", session.Id);
            return await BuildResponseAsync(session, slots, history, suspicion,
                aiMessage: ChatStrings.ReadyToBuild(lang), lang, ct);
        }

        // L3 suspicion: if score ≥ 50, skip Gemini to avoid burning tokens on adversarial sessions
        string aiMessage;
        SlotExtractorResult? extracted = null;
        AiCallDiagnostics? aiDiagnostics = null;
        var aiUnavailable = false;
        var preTurnSlotsJson = session.SlotsJson; // capture before merge for context_signals

        if (suspicion.ShouldSuppressGemini)
        {
            aiMessage = ChatStrings.OffTopicRedirect(lang, NextSlotQuestion(slots, lang));
        }
        else
        {
            (extracted, aiDiagnostics) = await _extractor.ExtractAsync(message, slots, history, lang, ct);

            if (extracted == null)
            {
                // Distinguir un fallo real de la cadena LLM (ErrorCode poblado: http_error,
                // timeout, truncated, provider_error, parse_error…) de un "no te he entendido"
                // legítimo (cadena OK pero el modelo no devolvió slots parseables). El fallo de
                // infra da un mensaje genérico de indisponibilidad + flag, sin exponer detalles.
                if (aiDiagnostics?.ErrorCode != null)
                {
                    aiUnavailable = true;
                    aiMessage = ChatStrings.AiUnavailable(lang);
                }
                else
                {
                    aiMessage = ChatStrings.ParseFallback(lang);
                }
            }
            else
            {
                bool hasContradiction = MergeSlots(slots, extracted.Extracted, acknowledge: true, lang, out string? ackPrefix);

                // L5: validate output
                var drift = OutputValidator.Inspect(extracted.AiMessage);
                if (drift == OutputValidator.DriftKind.CanaryLeak)
                {
                    suspicion.RecordCanaryLeak();
                    _secLog.CanaryLeak(session.Id);
                    return await QuarantineSessionAsync(session, slots, suspicion, lang, ct, "canary_leak");
                }
                else if (drift != OutputValidator.DriftKind.None)
                {
                    suspicion.RecordDrift(drift.ToString());
                    _secLog.DriftDetected(session.Id, drift.ToString(), suspicion.Score);
                    aiMessage = ChatStrings.GotItPrefix(lang, NextSlotQuestion(slots, lang));
                }
                else
                {
                    aiMessage = (hasContradiction && ackPrefix != null)
                        ? $"{ackPrefix} {extracted.AiMessage}"
                        : extracted.AiMessage;
                }
            }
        }

        // Coverage gate: si la extracción (o un FavoriteCity de perfil) coló una
        // ciudad no cubierta, no seguimos el slot-filling — avisamos y ofrecemos
        // una ciudad LIVE. El slot se limpia para que la sesión siga pidiendo ciudad.
        if (!string.IsNullOrWhiteSpace(slots.City) && !_coverage.IsLive(slots.City))
        {
            _secLog.CityNotWhitelisted(session.Id, slots.City);
            return await BuildCityUnsupportedResponseAsync(
                session, slots, history, suspicion, slots.City!, lang, ct);
        }

        suspicion.RecordCleanTurn();
        history.Add(new HistoryEntry { Role = "assistant", Content = aiMessage, Timestamp = DateTimeOffset.UtcNow });

        if (aiDiagnostics != null)
        {
            _db.ChatTurns.Add(new ChatTurn
            {
                SessionId = session.Id,
                UserId = session.UserId,
                TurnIndex = session.TurnCount,
                AiProvider = aiDiagnostics.Provider,
                Model = aiDiagnostics.Model,
                PromptVersion = "slot-v1",
                UserMessage = string.IsNullOrWhiteSpace(rawMessage) ? null
                    : PiiRedactor.Redact(rawMessage.Length > 2000 ? rawMessage[..2000] : rawMessage),
                ContextSignalsJson = preTurnSlotsJson,
                PromptChars = aiDiagnostics.Prompt.Length,
                PromptExcerpt = PiiRedactor.Redact(
                    aiDiagnostics.Prompt.Length > 500 ? aiDiagnostics.Prompt[..500] : aiDiagnostics.Prompt),
                ResponseRaw = aiDiagnostics.ResponseRaw != null
                    ? PiiRedactor.Redact(aiDiagnostics.ResponseRaw) : null,
                FinishReason = aiDiagnostics.FinishReason,
                LatencyMs = aiDiagnostics.LatencyMs,
                InputTokens = aiDiagnostics.InputTokens,
                OutputTokens = aiDiagnostics.OutputTokens,
                ThinkingTokens = aiDiagnostics.ThinkingTokens,
                TotalTokens = aiDiagnostics.TotalTokens,
                CostUsd = aiDiagnostics.CostUsd,
                GeminiStatus = aiDiagnostics.HttpStatus,
                ErrorCode = aiDiagnostics.ErrorCode,
                ErrorMessage = aiDiagnostics.ErrorMessage,
                SlotCompleteness = (short)(CountFilledCriticalSlots(slots) * 20),
            });
        }

        return await BuildResponseAsync(session, slots, history, suspicion, aiMessage, lang, ct,
            geminiQuickReplies: extracted?.QuickReplies,
            error: aiUnavailable ? "ai_unavailable" : null);
    }
}
