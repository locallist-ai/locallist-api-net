using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LocalList.API.NET.Features.Chat.Services;

// Small shared helpers for ChatAgentService: salted IP hashing and the tolerant JSON
// deserialization of the persisted slots/history/suspicion blobs. Logic is identical to the
// original single-file version; only its location changed.
public partial class ChatAgentService
{
    private string? HashIp(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return null;
        var salt = _config["WAITLIST_IP_SALT"];
        if (string.IsNullOrEmpty(salt))
        {
            _logger.LogWarning("Chat: WAITLIST_IP_SALT not configured — IP hashing uses fallback salt");
            salt = "chat-fallback-salt-set-WAITLIST_IP_SALT-env";
        }
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(ip + salt));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    // Lectura tolerante a casing: el slot blob se persiste en PascalCase
    // (JsonSerializer.Serialize(slots)), pero leerlo case-insensitive evita
    // perder slots si la forma serializada cambia (p. ej. defaults web camelCase).
    private static readonly JsonSerializerOptions SlotsReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static ChatSlots DeserializeSlots(string json)
    {
        try { return JsonSerializer.Deserialize<ChatSlots>(json, SlotsReadOptions) ?? new(); }
        catch { return new(); }
    }

    private static List<HistoryEntry> DeserializeHistory(string json)
    {
        try { return JsonSerializer.Deserialize<List<HistoryEntry>>(json) ?? new(); }
        catch { return new(); }
    }

    private static SuspicionTracker DeserializeSuspicion(string json)
    {
        try { return JsonSerializer.Deserialize<SuspicionTracker>(json) ?? new(); }
        catch { return new(); }
    }
}
