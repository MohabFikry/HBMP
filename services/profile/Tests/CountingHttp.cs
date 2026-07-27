using System.Net;
using System.Text;
using System.Text.Json;
using Mersal.Profile.Infrastructure;

namespace Mersal.Profile.Tests;

/// <summary>
/// A stand-in for the sibling services, which also RECORDS the outgoing Authorization header.
///
/// <para>That recording is the point: the caller-token invariant is not "the code looks like it forwards the
/// bearer", it is "the header on the wire is the caller's". A fake that only counted calls would pass just as
/// happily against a service-account handler.</para>
/// </summary>
public sealed class CountingHttp(JsonDocument response) : IDisposable
{
    public int Calls { get; private set; }
    public List<string?> AuthorizationHeaders { get; } = [];
    public List<string?> Paths { get; } = [];

    private readonly RecordingHandler _handler = new();

    public CallerScopedHttp AsCallerScopedHttp()
    {
        _handler.Owner = this;
        _handler.Body = response.RootElement.GetRawText();
        return new CallerScopedHttp(new SingleClientFactory(_handler));
    }

    internal void Record(HttpRequestMessage request)
    {
        Calls++;
        AuthorizationHeaders.Add(
            request.Headers.TryGetValues("Authorization", out var v) ? string.Join(',', v) : null);
        Paths.Add(request.RequestUri?.PathAndQuery);
    }

    public void Dispose() => _handler.Dispose();

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public CountingHttp? Owner { get; set; }
        public string Body { get; set; } = "{}";

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Owner?.Record(request);
            // A small delay so parallel callers genuinely overlap — without it the memoization test would pass
            // even against an implementation that fetched twice, because the first call would already be done.
            await Task.Delay(10, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("http://test.invalid") };
    }
}
