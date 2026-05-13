namespace LocalList.API.NET.Features.Chat.Services;

/// <summary>
/// Layer 7 guardrail: typed log wrapper that enforces consistent ChatSec: prefix
/// for security events. Filterable in Railway via "ChatSec:" query.
/// Never logs raw user content — only metadata (sessionId, pattern class, scores).
/// </summary>
public sealed class ChatSecLogger
{
    private readonly ILogger<ChatSecLogger> _logger;

    public ChatSecLogger(ILogger<ChatSecLogger> logger) => _logger = logger;

    public void InjectionDetected(Guid sessionId, string patternClass, int suspicionScore)
        => _logger.LogWarning(
            "ChatSec: injection sessionId={Session} pattern={Pattern} score={Score}",
            sessionId, patternClass, suspicionScore);

    public void OffTopicDetected(Guid sessionId, int suspicionScore)
        => _logger.LogInformation(
            "ChatSec: offtopic sessionId={Session} score={Score}",
            sessionId, suspicionScore);

    public void DriftDetected(Guid sessionId, string kind, int suspicionScore)
        => _logger.LogWarning(
            "ChatSec: drift sessionId={Session} kind={Kind} score={Score}",
            sessionId, kind, suspicionScore);

    public void CanaryLeak(Guid sessionId)
        => _logger.LogCritical(
            "ChatSec: canary_leak sessionId={Session} — system prompt may have been reflected",
            sessionId);

    public void Quarantined(Guid sessionId, string reason, int score)
        => _logger.LogWarning(
            "ChatSec: quarantined sessionId={Session} reason={Reason} score={Score}",
            sessionId, reason, score);

    public void RateLimitHit(string policy, string partitionKey)
        => _logger.LogInformation(
            "ChatSec: rate_limit_hit policy={Policy} key={Key}",
            policy, partitionKey);

    public void AnonSessionMismatch(Guid sessionId)
        => _logger.LogInformation(
            "ChatSec: anon_session_ip_mismatch sessionId={Session} — treating as new session",
            sessionId);

    public void ChipForgeryAttempt(Guid sessionId, string chipId)
        => _logger.LogWarning(
            "ChatSec: chip_forgery sessionId={Session} chipId={ChipId}",
            sessionId, chipId);

    public void CityNotWhitelisted(Guid sessionId, string? attemptedCity)
        => _logger.LogInformation(
            "ChatSec: city_not_whitelisted sessionId={Session} city={City}",
            sessionId, attemptedCity ?? "(null)");
}
