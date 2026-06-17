using System.Net;
using System.Text;

namespace LocalList.API.Tests.Unit.Llm;

/// <summary>Handler fake para unit tests de providers: captura la request y devuelve una respuesta fija.</summary>
public sealed class CapturingHandler(HttpStatusCode status, string body) : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastRequestBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }
}

/// <summary>
/// Handler que simula fallos de transporte o resiliencia: timeout interno de HttpClient
/// (TaskCanceledException), Polly TimeoutRejectedException, red caída… Lanza la excepción dada.
/// </summary>
public sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromException<HttpResponseMessage>(exception);
}
