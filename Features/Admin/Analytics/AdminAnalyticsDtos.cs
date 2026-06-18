namespace LocalList.API.NET.Features.Admin.Analytics;

public record AdminChatTurnDto(
    Guid Id,
    DateTimeOffset CreatedAt,
    Guid? SessionId,
    Guid? UserId,
    int TurnIndex,
    string AiProvider,
    string Model,
    string PromptVersion,
    int PromptChars,
    string? FinishReason,
    int LatencyMs,
    int? InputTokens,
    int? OutputTokens,
    int? ThinkingTokens,
    int? TotalTokens,
    decimal? CostUsd,
    int? GeminiStatus,
    string? ErrorCode,
    string? ErrorMessage,
    short? SlotCompleteness);

public record AdminChatTurnsListResponse(
    List<AdminChatTurnDto> Turns,
    int Total,
    int Limit,
    int Offset);

public record AdminChatTurnsStatsDto(
    int TotalTurns,
    double AvgLatencyMs,
    long TotalInputTokens,
    long TotalOutputTokens,
    long TotalThinkingTokens,
    decimal TotalCostUsd,
    double AvgSlotCompleteness,
    double ErrorRate,
    Dictionary<string, int> FinishReasonBreakdown,
    Dictionary<string, int> ErrorCodeBreakdown);

public record AdminPlanMetricDto(
    Guid Id,
    DateTimeOffset CreatedAt,
    Guid PlanId,
    string? PlanName,
    string? PlanCity,
    string GenerationSource,
    short SignalsFilled,
    int NumDays,
    int NumStops,
    int NumCategories,
    string? GroupType,
    string? Budget,
    int LatencyMs,
    decimal? CostUsd,
    bool WasOpened,
    DateTimeOffset? OpenedAt,
    bool WasFollowed,
    DateTimeOffset? FollowedAt,
    int EditedCount,
    bool Regenerated);

public record AdminPlanMetricsListResponse(
    List<AdminPlanMetricDto> Metrics,
    int Total,
    int Limit,
    int Offset);

public record AdminPlanMetricsStatsDto(
    int TotalPlans,
    double OpenRate,
    double FollowRate,
    double AvgLatencyMs,
    decimal TotalCostUsd,
    List<AdminPlanMetricsByCityDto> ByCity);

public record AdminPlanMetricsByCityDto(
    string City,
    int Count,
    double OpenRate,
    double FollowRate);
