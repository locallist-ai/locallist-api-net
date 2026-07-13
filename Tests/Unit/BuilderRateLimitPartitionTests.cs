using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using LocalList.API.NET.Shared.Startup;

namespace LocalList.API.Tests.Unit;

/// <summary>
/// Tests unitarios de la decisión de partición del rate-limit de los endpoints caros de
/// generación (<c>/builder/chat</c> y <c>/chat/generate</c>, política <c>BuilderLimit</c>).
///
/// El fix de raíz hace la política identity-aware: un usuario autenticado obtiene un bucket
/// propio (por userId) con límite más alto, mientras que el tráfico anónimo comparte un
/// bucket por IP con límite estricto anti-abuso de coste Gemini. Estos tests fijan ese
/// contrato sin necesidad de levantar un limiter real.
/// </summary>
public class BuilderRateLimitPartitionTests
{
    [Fact]
    public void ResolveBuilderPartition_Authenticated_UsesOwnBucketAndHigherLimit()
    {
        var (key, limit) = RateLimitingExtensions.ResolveBuilderPartition(
            userId: "user-123", ip: "203.0.113.7", anonLimit: 5, authLimit: 20);

        Assert.Equal("builder_auth_user-123", key);
        Assert.Equal(20, limit);
    }

    [Fact]
    public void ResolveBuilderPartition_Anonymous_UsesIpBucketAndAnonLimit()
    {
        var (key, limit) = RateLimitingExtensions.ResolveBuilderPartition(
            userId: null, ip: "203.0.113.7", anonLimit: 5, authLimit: 20);

        Assert.Equal("builder_anon_203.0.113.7", key);
        Assert.Equal(5, limit);
    }

    [Fact]
    public void ResolveBuilderPartition_AnonymousWithoutIp_FallsBackToUnknown()
    {
        var (key, limit) = RateLimitingExtensions.ResolveBuilderPartition(
            userId: null, ip: null, anonLimit: 5, authLimit: 20);

        Assert.Equal("builder_anon_unknown", key);
        Assert.Equal(5, limit);
    }

    [Fact]
    public void ResolveBuilderPartition_EmptyUserId_TreatedAsAnonymous()
    {
        var (key, limit) = RateLimitingExtensions.ResolveBuilderPartition(
            userId: "", ip: "198.51.100.4", anonLimit: 5, authLimit: 20);

        Assert.Equal("builder_anon_198.51.100.4", key);
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
        // Un usuario autenticado y el tráfico anónimo de su misma IP no deben colisionar:
        // el prefijo distinto ("builder_auth_" vs "builder_anon_") garantiza aislamiento.
        var (authKey, _) = RateLimitingExtensions.ResolveBuilderPartition("42", "192.0.2.1", 5, 20);
        var (anonKey, _) = RateLimitingExtensions.ResolveBuilderPartition(null, "42", 5, 20);

        Assert.NotEqual(authKey, anonKey);
    }

    [Fact]
    public void ExtractUserId_ReadsNameIdentifierClaim()
    {
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, "nid-1") }, "test"))
        };

        Assert.Equal("nid-1", RateLimitingExtensions.ExtractUserId(ctx));
    }

    [Fact]
    public void ExtractUserId_FallsBackToSubClaim()
    {
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim("sub", "sub-1") }, "test"))
        };

        Assert.Equal("sub-1", RateLimitingExtensions.ExtractUserId(ctx));
    }

    [Fact]
    public void ExtractUserId_NoIdentityClaims_ReturnsNull()
    {
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity())
        };

        Assert.Null(RateLimitingExtensions.ExtractUserId(ctx));
    }
}
