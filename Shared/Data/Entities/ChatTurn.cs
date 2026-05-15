using System.ComponentModel.DataAnnotations.Schema;

namespace LocalList.API.NET.Shared.Data.Entities;

[Table("chat_turns")]
public class ChatTurn
{
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("user_id")]
    public Guid? UserId { get; set; }

    [Column("session_id")]
    public Guid? SessionId { get; set; }

    [Column("turn_index")]
    public int TurnIndex { get; set; }

    [Column("trace_id")]
    public string? TraceId { get; set; }

    [Column("ai_provider")]
    public string AiProvider { get; set; } = string.Empty;

    [Column("model")]
    public string Model { get; set; } = "gemini-2.5-flash";

    [Column("prompt_version")]
    public string PromptVersion { get; set; } = string.Empty;

    [Column("user_message")]
    public string? UserMessage { get; set; }

    [Column("quick_reply_id")]
    public string? QuickReplyId { get; set; }

    [Column("context_signals", TypeName = "jsonb")]
    public string? ContextSignalsJson { get; set; }

    [Column("prompt_chars")]
    public int PromptChars { get; set; }

    [Column("prompt_excerpt")]
    public string PromptExcerpt { get; set; } = string.Empty;

    [Column("response_raw")]
    public string? ResponseRaw { get; set; }

    [Column("finish_reason")]
    public string? FinishReason { get; set; }

    [Column("latency_ms")]
    public int LatencyMs { get; set; }

    [Column("input_tokens")]
    public int? InputTokens { get; set; }

    [Column("output_tokens")]
    public int? OutputTokens { get; set; }

    [Column("thinking_tokens")]
    public int? ThinkingTokens { get; set; }

    [Column("total_tokens")]
    public int? TotalTokens { get; set; }

    [Column("cost_usd")]
    public decimal? CostUsd { get; set; }

    [Column("gemini_status")]
    public int? GeminiStatus { get; set; }

    [Column("error_code")]
    public string? ErrorCode { get; set; }

    [Column("error_message")]
    public string? ErrorMessage { get; set; }

    [Column("slot_completeness")]
    public short? SlotCompleteness { get; set; }

    [Column("regenerated")]
    public bool Regenerated { get; set; }

    // Nav properties (nullable FKs)
    public User? User { get; set; }
    public ChatSession? Session { get; set; }
}
