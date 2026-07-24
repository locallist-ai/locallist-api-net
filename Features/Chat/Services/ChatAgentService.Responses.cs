using System.Text.Json;
using LocalList.API.NET.Features.Chat.I18n;
using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.NET.Features.Chat.Services;

// Response construction and quarantine transitions for ChatAgentService: the shared
// BuildResponseAsync persistence path, canned/quarantine/city-unsupported replies and the
// session-quarantine mutation. Logic is identical to the original single-file version; only
// its location changed.
public partial class ChatAgentService
{
    private async Task<ChatTurnResponse> BuildResponseAsync(
        ChatSession session,
        ChatSlots slots,
        List<HistoryEntry> history,
        SuspicionTracker suspicion,
        string? aiMessage,
        string lang,
        CancellationToken ct,
        List<ChatQuickReply>? geminiQuickReplies = null,
        string? error = null)
    {
        var missing = GetMissingCritical(slots);
        var ready = missing.Count == 0;

        List<ChatQuickReply> quickReplies = geminiQuickReplies ?? new();

        if (error == null)
        {
            if (ready && !AreAllTier2Filled(slots) && quickReplies.Count == 0)
            {
                (aiMessage, quickReplies) = BuildTier2Question(slots, lang);
                ready = false;
            }
            else if (ready && quickReplies.Count == 0)
            {
                aiMessage ??= ChatStrings.ReadyToBuild(lang);
            }

            if (session.TurnCount + 1 >= TurnLimit) ready = true;
        }
        else
        {
            // Fallo de infra: no avanzar a ready/tier2 ni forzar ready por turn cap;
            // conservar el mensaje genérico de indisponibilidad.
            ready = false;
        }

        // L6: sanitize aiMessage before persisting and returning
        aiMessage = OutputSanitizer.Sanitize(aiMessage ?? ChatStrings.GreetingNoCity(lang));

        var wasAlreadyReady = session.Status == "ready";
        session.TurnCount++;
        session.LastTurnAt = DateTimeOffset.UtcNow;
        session.Status = ready ? "ready" : "active";
        session.SlotsJson = JsonSerializer.Serialize(slots);
        session.SuspicionJson = JsonSerializer.Serialize(suspicion);
        session.LastOfferedChips = quickReplies.Select(qr => qr.Id).ToArray();

        var trimmedHistory = history.TakeLast(20).ToList();
        session.HistoryJson = JsonSerializer.Serialize(trimmedHistory);

        _db.Update(session);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Chat: turn={Turn} sessionId={Session} missing=[{Missing}] ready={Ready} suspicion={Score}",
            session.TurnCount, session.Id, string.Join(",", missing), ready, suspicion.Score);

        if (ready && !wasAlreadyReady)
        {
            var readyDistinctId = session.UserId?.ToString() ?? session.AnonymousIpHash ?? session.Id.ToString();
            _ = _posthog.CaptureAsync(readyDistinctId, "chat_ready", new()
            {
                ["session_id"] = session.Id.ToString(),
                ["turn_count"] = session.TurnCount,
                ["city"] = slots.City,
                ["days"] = (object?)slots.Days,
            });
        }

        return new ChatTurnResponse
        {
            SessionId = session.Id,
            AiMessage = aiMessage,
            Slots = slots,
            MissingCritical = missing,
            QuickReplies = quickReplies.Take(4).ToList(),
            Ready = ready,
            Error = error,
            TurnCount = session.TurnCount,
            TurnLimit = TurnLimit
        };
    }

    private ChatTurnResponse BuildCannedResponse(
        ChatSession session, ChatSlots slots, string message, string? nextSlot, string lang = "en")
    {
        return new ChatTurnResponse
        {
            SessionId = session.Id,
            AiMessage = OutputSanitizer.Sanitize(message),
            Slots = slots,
            MissingCritical = GetMissingCritical(slots),
            QuickReplies = nextSlot != null ? QuickRepliesForSlot(nextSlot, lang) : new(),
            Ready = false,
            TurnCount = session.TurnCount,
            TurnLimit = TurnLimit
        };
    }

    /// <summary>
    /// Respuesta cuando la ciudad pedida no está en la cobertura LIVE. Limpia el
    /// slot de ciudad (la sesión vuelve a "necesita ciudad"), persiste el estado y
    /// devuelve <c>cityUnsupported=true</c> sin avanzar el slot-filling.
    /// </summary>
    private async Task<ChatTurnResponse> BuildCityUnsupportedResponseAsync(
        ChatSession session, ChatSlots slots, List<HistoryEntry> history,
        SuspicionTracker suspicion, string attemptedCity, string lang, CancellationToken ct)
    {
        slots.City = null;

        var message = OutputSanitizer.Sanitize(
            ChatStrings.CityUnsupported(lang, attemptedCity, _coverage.LiveCities));
        history.Add(new HistoryEntry { Role = "assistant", Content = message, Timestamp = DateTimeOffset.UtcNow });

        session.LastTurnAt = DateTimeOffset.UtcNow;
        session.Status = "active";
        session.SlotsJson = JsonSerializer.Serialize(slots);
        session.SuspicionJson = JsonSerializer.Serialize(suspicion);
        session.HistoryJson = JsonSerializer.Serialize(history.TakeLast(20).ToList());
        session.LastOfferedChips = Array.Empty<string>();
        _db.Update(session);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Chat: city not covered sessionId={Session} attempted={City}",
            session.Id, attemptedCity);

        return new ChatTurnResponse
        {
            SessionId = session.Id,
            AiMessage = message,
            Slots = slots,
            MissingCritical = GetMissingCritical(slots),
            QuickReplies = new(),
            Ready = false,
            CityUnsupported = true,
            TurnCount = session.TurnCount,
            TurnLimit = TurnLimit
        };
    }

    private ChatTurnResponse BuildQuarantineResponse(ChatSession session, ChatSlots slots, string lang = "en")
    {
        return new ChatTurnResponse
        {
            SessionId = session.Id,
            AiMessage = ChatStrings.Quarantine(lang),
            Slots = slots,
            MissingCritical = GetMissingCritical(slots),
            QuickReplies = new(),
            Ready = false,
            Quarantined = true,
            TurnCount = session.TurnCount,
            TurnLimit = TurnLimit
        };
    }

    private async Task<ChatTurnResponse> QuarantineSessionAsync(
        ChatSession session, ChatSlots slots, SuspicionTracker suspicion,
        string lang, CancellationToken ct, string reason)
    {
        suspicion.LastTrigger = reason;
        _secLog.Quarantined(session.Id, reason, suspicion.Score);

        session.Status = "quarantined";
        session.SuspicionJson = JsonSerializer.Serialize(suspicion);
        _db.Update(session);
        await _db.SaveChangesAsync(ct);

        return BuildQuarantineResponse(session, slots, lang);
    }
}
