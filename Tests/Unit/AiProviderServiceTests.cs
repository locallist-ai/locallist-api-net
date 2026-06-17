using LocalList.API.NET.Shared.AI.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalList.API.Tests.Unit;

public class DescriptionGeneratorServiceTests
{
    [Fact]
    public async Task GeneratePlaceDescriptionWithDiagnostics_WhenKeyMissing_ReturnsMissingKeyKind()
    {
        var config = new ConfigurationBuilder().Build(); // no Gemini:ApiKey set
        var svc = new DescriptionGeneratorService(new HttpClient(), config, NullLogger<DescriptionGeneratorService>.Instance);

        var result = await svc.GeneratePlaceDescriptionWithDiagnosticsAsync(
            "Test Place", "Miami", "Food", null, null, null, null, null);

        Assert.Null(result.Description);
        Assert.Equal("missing_key", result.ErrorKind);
    }
}
