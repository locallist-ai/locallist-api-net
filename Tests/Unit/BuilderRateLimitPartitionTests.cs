using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using LocalList.API.NET.Features.Auth.Services;
using LocalList.API.NET.Shared.Startup;

namespace LocalList.API.Tests.Unit;

/// <summary>
/// Tests unitarios de la decisión de partición y del tipo de limiter del rate-limit de los
/// endpoints caros de generación (<c>/builder/chat</c> y <c>/chat/generate</c>, política
/// <c>BuilderLimit</c>).
///
/// El fix de raíz combina: (a) un techo por IP encadenado que acota el account-farming,
/// (b) un refinamiento por identidad donde SOLO los tokens de la app (AppScheme) obtienen el
/// bucket alto, y (c) sliding window en vez de ventana fija (anti boundary-doubling).
/// </summary>
public class BuilderRateLimitPartitionTests
{
    // ── ResolveBuilderPartition ──────────────────────────────────────────────────────

    [Fact]
    public void ResolveBuilderPartition_AppAuthenticated_UsesOwnBucketAndHigherLimit()
    {
        var (key, limit) = RateLimitingExtensions.ResolveBuilderPartition(
            appUserId: "user-123", ip: "203.0.113.7", anonLimit: 5, authLimit: 20);

        Assert.Equal("builder_auth_user-123", key);
        Assert.Equal(20, limit);
    }

    [Fact]
    public void ResolveBuilderPartition_Anonymous_UsesIpBucketAndAnonLimit()
    {
        var (key, limit) = RateLimitingExtensions.ResolveBuilderPartition(
            appUserId: null, ip: "203.0.113.7", anonLimit: 5, authLimit: 20);

        Assert.Equal("builder_anon_203.0.113.7", key);
        Assert.Equal(5, limit);
    }

    [Fact]
    public void ResolveBuilderPartition_AnonymousWithoutIp_FallsBackToUnknown()
    {
        var (key, limit) = RateLimitingExtensions.ResolveBuilderPartition(
            appUserId: null, ip: null, anonLimit: 5, authLimit: 20);

        Assert.Equal("builder_anon_unknown", key);
        Assert.Equal(5, limit);
    }

    [Fact]
    public void ResolveBuilderPartition_DifferentUsers_GetDistinctBuckets()
    {
        var (keyA, _) = RateLimitingExtensions.ResolveBuilderPartition("alice", "10.0.0.1", 5, 20);
        var (keyB, _) = RateLimitingExtensions.ResolveBuilderPartition("bob", "10.0.0.1", 5, 20);

        Assert.NotEqual(keyA, keyB);
    }

    [Fact]
    public void ResolveBuilderPartition_AuthAndAnon_NeverShareAKey()
    {
        var (authKey, _) = RateLimitingExtensions.ResolveBuilderPartition("42", "192.0.2.1", 5, 20);
        var (anonKey, _) = RateLimitingExtensions.ResolveBuilderPartition(null, "42", 5, 20);

        Assert.NotEqual(authKey, anonKey);
    }

    // ── ExtractAppUserId: solo AppScheme obtiene identidad (bucket alto) ─────────────

    [Fact]
    public void ExtractAppUserId_AppIssuerToken_ReturnsUserId()
    {
        var ctx = ContextWithClaim(
            new Claim(ClaimTypes.NameIdentifier, "app-user-1", ClaimValueTypes.String, JwtTokenService.Issuer));

        Assert.Equal("app-user-1", RateLimitingExtensions.ExtractAppUserId(ctx));
    }

    [Fact]
    public void ExtractAppUserId_AppIssuerSubClaim_ReturnsUserId()
    {
        var ctx = ContextWithClaim(
            new Claim("sub", "app-sub-1", ClaimValueTypes.String, JwtTokenService.Issuer));

        Assert.Equal("app-sub-1", RateLimitingExtensions.ExtractAppUserId(ctx));
    }

    [Fact]
    public void ExtractAppUserId_FirebaseIssuerToken_ReturnsNull()
    {
        // Un token Firebase (issuer distinto) NO obtiene identidad → cae al bucket anónimo.
        // Cierra el bypass de Firebase Anonymous Auth (UIDs ilimitados).
        var ctx = ContextWithClaim(
            new Claim(ClaimTypes.NameIdentifier, "firebase-uid-1", ClaimValueTypes.String,
                "https://securetoken.google.com/some-project"));

        Assert.Null(RateLimitingExtensions.ExtractAppUserId(ctx));
    }

    [Fact]
    public void ExtractAppUserId_NoIdentity_ReturnsNull()
    {
        var ctx = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) };

        Assert.Null(RateLimitingExtensions.ExtractAppUserId(ctx));
    }

    // ── ResolveChatTurnPartition: mismo contrato que Builder con prefijos chat_ ─────

    [Fact]
    public void ResolveChatTurnPartition_AppAuthenticated_UsesOwnBucketAndHigherLimit()
    {
        var (key, limit) = RateLimitingExtensions.ResolveChatTurnPartition(
            appUserId: "user-9", ip: "203.0.113.9", anonLimit: 20, authLimit: 40);

        Assert.Equal("chat_auth_user-9", key);
        Assert.Equal(40, limit);
    }

    [Fact]
    public void ResolveChatTurnPartition_Anonymous_UsesIpBucketAndAnonLimit()
    {
        var (key, limit) = RateLimitingExtensions.ResolveChatTurnPartition(
            appUserId: null, ip: "203.0.113.9", anonLimit: 20, authLimit: 40);

        Assert.Equal("chat_anon_203.0.113.9", key);
        Assert.Equal(20, limit);
    }

    [Fact]
    public void ResolveChatTurnPartition_AuthAndAnon_NeverShareAKey()
    {
        var (authKey, _) = RateLimitingExtensions.ResolveChatTurnPartition("7", "192.0.2.1", 20, 40);
        var (anonKey, _) = RateLimitingExtensions.ResolveChatTurnPartition(null, "7", 20, 40);

        Assert.NotEqual(authKey, anonKey);
    }

    // ── ResolveMeteredIpCeiling: qué endpoints van bajo el techo por IP ─────────────

    [Fact]
    public void ResolveMeteredIpCeiling_BuilderPolicy_UsesBuilderNamespaceAndCeiling()
    {
        var ctx = ContextWithPolicy("BuilderLimit");

        var (prefix, ceiling) = RateLimitingExtensions.ResolveMeteredIpCeiling(ctx, 60, 120);

        Assert.Equal("builder_ip_ceiling", prefix);
        Assert.Equal(60, ceiling);
    }

    [Fact]
    public void ResolveMeteredIpCeiling_ChatTurnPolicy_UsesChatTurnNamespaceAndCeiling()
    {
        var ctx = ContextWithPolicy("ChatTurnLimit");

        var (prefix, ceiling) = RateLimitingExtensions.ResolveMeteredIpCeiling(ctx, 60, 120);

        Assert.Equal("chatturn_ip_ceiling", prefix);
        Assert.Equal(120, ceiling);
    }

    [Fact]
    public void ResolveMeteredIpCeiling_OtherOrNoPolicy_NotMetered()
    {
        Assert.Equal((null, 0), RateLimitingExtensions.ResolveMeteredIpCeiling(
            ContextWithPolicy("AuthLimit"), 60, 120));
        Assert.Equal((null, 0), RateLimitingExtensions.ResolveMeteredIpCeiling(
            new DefaultHttpContext(), 60, 120));
    }

    // ── Sliding window (anti boundary-doubling) ─────────────────────────────────────

    [Fact]
    public void CreateSlidingHourlyLimiter_IsSlidingWindow_NotFixed()
    {
        using var limiter = RateLimitingExtensions.CreateSlidingHourlyLimiter(20);
        Assert.IsType<SlidingWindowRateLimiter>(limiter);
    }

    private static DefaultHttpContext ContextWithClaim(Claim claim) =>
        new() { User = new ClaimsPrincipal(new ClaimsIdentity(new[] { claim }, "test")) };

    private static DefaultHttpContext ContextWithPolicy(string policyName)
    {
        var ctx = new DefaultHttpContext();
        var endpoint = new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(
                new Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute(policyName)),
            "test-endpoint");
        ctx.SetEndpoint(endpoint);
        return ctx;
    }
}
