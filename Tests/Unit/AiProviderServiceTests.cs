using LocalList.API.NET.Features.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalList.API.Tests.Unit;

public class AiProviderServiceTests
{
    [Fact]
    public async Task GeneratePlaceDescriptionWithDiagnostics_WhenKeyMissing_ReturnsMissingKeyKind()
    {
        var config = new ConfigurationBuilder().Build(); // no Gemini:ApiKey set
        var svc = new AiProviderService(new HttpClient(), config, NullLogger<AiProviderService>.Instance);

        var result = await svc.GeneratePlaceDescriptionWithDiagnosticsAsync(
            "Test Place", "Miami", "Food", null, null, null, null, null);

        Assert.Null(result.Description);
        Assert.Equal("missing_key", result.ErrorKind);
    }
}
