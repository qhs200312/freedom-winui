using System.Net.Http;

namespace ServiceLib.Tests.Services.Diagnostics;

internal sealed class DiagnosticFixtureHandler(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public ConcurrentQueue<string> RequestedUrls { get; } = new();
    public bool WasDisposed { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestedUrls.Enqueue(request.RequestUri!.ToString());
        var response = await respond(request, cancellationToken);
        response.RequestMessage ??= request;
        return response;
    }

    public static DiagnosticFixtureHandler Create(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new((request, _) => Task.FromResult(respond(request)));

    public static HttpResponseMessage Response(string body = "", HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Redirect(string location, HttpStatusCode status = HttpStatusCode.Found)
    {
        var response = Response(status: status);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    protected override void Dispose(bool disposing)
    {
        WasDisposed = true;
        base.Dispose(disposing);
    }
}
