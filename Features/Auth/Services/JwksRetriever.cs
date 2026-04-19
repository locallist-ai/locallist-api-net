using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace LocalList.API.NET.Features.Auth.Services;

/// <summary>
/// Retrieves a JWKS document directly from the given URL and wraps it in
/// an OpenIdConnectConfiguration. Used for providers (Apple, Google) that
/// expose raw JWKS endpoints rather than the full OpenID Connect discovery.
/// </summary>
internal class JwksRetriever : IConfigurationRetriever<OpenIdConnectConfiguration>
{
    public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(
        string address, IDocumentRetriever retriever, CancellationToken cancel)
    {
        var jwks = await retriever.GetDocumentAsync(address, cancel);
        var keys = new JsonWebKeySet(jwks);
        var config = new OpenIdConnectConfiguration();
        foreach (var key in keys.GetSigningKeys())
            config.SigningKeys.Add(key);
        return config;
    }
}
