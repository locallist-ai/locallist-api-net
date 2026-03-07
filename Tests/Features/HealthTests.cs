namespace LocalList.API.Tests.Features;

public class HealthTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task Health_ReturnsOk()
    {
        var client = fixture.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<HealthResponse>();
        Assert.NotNull(body);
        Assert.Equal("ok", body.Status);
        Assert.Equal("0.1.0", body.Version);
    }

    private record HealthResponse(string Status, string Version, DateTimeOffset Timestamp);
}
