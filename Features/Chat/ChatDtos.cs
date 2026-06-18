using System.ComponentModel.DataAnnotations;

namespace LocalList.API.NET.Features.Chat;

public class PreSeededSlots
{
    [MaxLength(60)]
    public string? City { get; set; }
}

public class ChatTurnRequest
{
    public Guid? SessionId { get; set; }

    [MaxLength(500)]
    public string? Message { get; set; }

    [MaxLength(50)]
    public string? QuickReplyId { get; set; }

    /// <summary>
    /// Optional slots pre-filled by the client (e.g. city selected before opening chat).
    /// Only honored on the very first turn (SessionId == null). Ignored on subsequent turns.
    /// </summary>
    public PreSeededSlots? PreSeededSlots { get; set; }
}

public class ChatQuickReply
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public bool MultiSelect { get; set; } = false;
}

public class ChatSlots
{
    public string? City { get; set; }
    public int? Days { get; set; }
    public string? GroupType { get; set; }
    public List<string> Categories { get; set; } = new();
    public string? Budget { get; set; }         // budget | moderate | premium
    public string? Pace { get; set; }           // slow | normal | fast
    public List<string> Dietary { get; set; } = new();
    public List<string> Exclusions { get; set; } = new();
    public string? VibesPrimary { get; set; }
    // Tier 3 — bonus only
    public string? AccommodationArea { get; set; }
    public string? Mobility { get; set; }
    public string? TimeOfDay { get; set; }      // early_bird | night_owl
}

public class ChatTurnResponse
{
    public Guid SessionId { get; set; }
    public string AiMessage { get; set; } = string.Empty;
    public ChatSlots Slots { get; set; } = new();
    public List<string> MissingCritical { get; set; } = new();
    public List<ChatQuickReply> QuickReplies { get; set; } = new();
    public bool Ready { get; set; }
    public bool Quarantined { get; set; }
    /// <summary>True cuando la ciudad pedida no está en la cobertura LIVE: el slot-filling se detiene hasta que se elige una ciudad cubierta.</summary>
    public bool CityUnsupported { get; set; }
    /// <summary>Código de error de cara al cliente cuando un fallo de infra impide procesar el turno (p. ej. "ai_unavailable"). Null en operación normal (se omite del JSON).</summary>
    public string? Error { get; set; }
    public int TurnCount { get; set; }
    public int TurnLimit { get; set; } = 6;
}

public class ChatGenerateRequest
{
    [Required]
    public Guid SessionId { get; set; }
}

// Extractor JSON schema that Gemini must return
public class SlotExtractorResult
{
    public ChatSlots Extracted { get; set; } = new();
    public string AiMessage { get; set; } = string.Empty;
    public string? NextQuestion { get; set; }
    public List<ChatQuickReply> QuickReplies { get; set; } = new();
}
