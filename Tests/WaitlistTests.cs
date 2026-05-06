namespace LocalList.API.Tests;

public class WaitlistTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fixture;
    private readonly HttpClient _client;

    public WaitlistTests(ApiFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.CreateClient();
    }

    [Fact]
    public async Task Post_ValidEmail_Returns201AndIncrementsCount()
    {
        var email = $"test-{Guid.NewGuid():N}@example.com";

        var countBefore = await GetCount();

        var response = await _client.PostAsJsonAsync("/waitlist", new { email });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<WaitlistResponse>();
        Assert.NotNull(body);
        Assert.Equal("Successfully joined the waitlist", body.Message);
        Assert.Equal(countBefore + 1, body.Position);
    }

    [Fact]
    public async Task Post_WithUtmParams_Returns201()
    {
        var email = $"utm-{Guid.NewGuid():N}@example.com";

        var response = await _client.PostAsJsonAsync("/waitlist", new
        {
            email,
            utmSource = "tiktok",
            utmMedium = "bio",
            utmCampaign = "launch",
            utmContent = "pablo",
            referrer = "https://www.tiktok.com/@pablo.locallist",
            landingPath = "/pablo",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Post_DuplicateEmail_Returns201WithoutError()
    {
        var email = $"dup-{Guid.NewGuid():N}@example.com";

        // First signup
        var first = await _client.PostAsJsonAsync("/waitlist", new { email });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var countAfterFirst = await GetCount();

        // Second signup — ON CONFLICT DO UPDATE updates last_touch_at, count stays same
        var second = await _client.PostAsJsonAsync("/waitlist", new
        {
            email,
            utmSource = "instagram",
            utmMedium = "story",
        });
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        var countAfterSecond = await GetCount();
        Assert.Equal(countAfterFirst, countAfterSecond);
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("missing@")]
    [InlineData("@nodomain.com")]
    [InlineData("spaces in@email.com")]
    public async Task Post_InvalidEmail_Returns400(string invalidEmail)
    {
        var response = await _client.PostAsJsonAsync("/waitlist", new { email = invalidEmail });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_EmptyEmail_Returns400()
    {
        var response = await _client.PostAsJsonAsync("/waitlist", new { email = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_NullBody_Returns400()
    {
        var response = await _client.PostAsJsonAsync<object?>("/waitlist", null);

        // Controller model binding should reject null body
        Assert.True(
            response.StatusCode == HttpStatusCode.BadRequest ||
            response.StatusCode == HttpStatusCode.UnsupportedMediaType);
    }

    [Fact]
    public async Task GetCount_ReturnsCorrectCount()
    {
        // Insert a known email
        var email = $"count-{Guid.NewGuid():N}@example.com";
        await _client.PostAsJsonAsync("/waitlist", new { email });

        var response = await _client.GetAsync("/waitlist/count");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<CountResponse>();
        Assert.NotNull(body);
        Assert.True(body.Count > 0);
    }

    private async Task<int> GetCount()
    {
        var response = await _client.GetAsync("/waitlist/count");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<CountResponse>();
        return body!.Count;
    }

    private record WaitlistResponse(string Message, int Position);
    private record CountResponse(int Count);
}
