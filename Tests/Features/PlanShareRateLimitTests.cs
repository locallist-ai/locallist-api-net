using System.Net;
using LocalList.API.NET.Features.Plans;
using LocalList.API.NET.Shared.Data.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace LocalList.API.Tests.Features;

/// <summary>
/// Fixture con el rate-limiter REAL activo (no GetNoLimiter) y <c>SharedPlan:RateLimitPerMinute=2</c>
/// para observar el corte por IP de <c>GET /plans/shared/{token}</c>. Reusa el
/// <see cref="TestClientIpStartupFilter"/> para simular IPs distintas (aísla los buckets).
/// </summary>
public sealed class SharedPlanRateLimitFixture : ApiFixture
{
    protected override bool DisableRateLimiting => false;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("SharedPlan:RateLimitPerMinute", "2");
        builder.ConfigureTestServices(s =>
            s.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, TestClientIpStartupFilter>());
        base.ConfigureWebHost(builder);
    }
}

public class PlanShareRateLimitTests : IClassFixture<SharedPlanRateLimitFixture>
{
    private readonly SharedPlanRateLimitFixture _fixture;
    public PlanShareRateLimitTests(SharedPlanRateLimitFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task GetShared_ExceedsPerIpLimit_Returns429()
    {
        var owner = Guid.NewGuid();
        var db = _fixture.GetDbContext();
        db.Users.Add(new User { Id = owner, Email = $"rl-share-{owner:N}@test.com", FirebaseUid = $"fb-rl-{owner:N}" });
        var token = ShareTokenGenerator.Generate();
        db.Plans.Add(new Plan
        {
            Id = Guid.NewGuid(), Name = "RL Shared", City = "Miami", Type = "custom",
            DurationDays = 1, Visibility = "unlisted", ShareToken = token, CreatedById = owner,
        });
        await db.SaveChangesAsync();

        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Client-Ip", "10.7.7.7");

        // Límite = 2/min: las dos primeras pasan (200), la tercera se rechaza (429).
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await client.GetAsync($"/plans/shared/{token}")).StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await client.GetAsync($"/plans/shared/{token}")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync($"/plans/shared/{token}")).StatusCode);
    }

    [Fact]
    public async Task GetShared_PerIpBucket_OtherIpNotBlocked()
    {
        var owner = Guid.NewGuid();
        var db = _fixture.GetDbContext();
        db.Users.Add(new User { Id = owner, Email = $"rl-share2-{owner:N}@test.com", FirebaseUid = $"fb-rl2-{owner:N}" });
        var token = ShareTokenGenerator.Generate();
        db.Plans.Add(new Plan
        {
            Id = Guid.NewGuid(), Name = "RL Shared 2", City = "Miami", Type = "custom",
            DurationDays = 1, Visibility = "unlisted", ShareToken = token, CreatedById = owner,
        });
        await db.SaveChangesAsync();

        var flooder = _fixture.CreateClient();
        flooder.DefaultRequestHeaders.Add("X-Test-Client-Ip", "10.7.7.8");
        await flooder.GetAsync($"/plans/shared/{token}");
        await flooder.GetAsync($"/plans/shared/{token}");
        Assert.Equal(HttpStatusCode.TooManyRequests, (await flooder.GetAsync($"/plans/shared/{token}")).StatusCode);

        // Otra IP tiene bucket independiente.
        var other = _fixture.CreateClient();
        other.DefaultRequestHeaders.Add("X-Test-Client-Ip", "10.7.7.9");
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await other.GetAsync($"/plans/shared/{token}")).StatusCode);
    }
}
