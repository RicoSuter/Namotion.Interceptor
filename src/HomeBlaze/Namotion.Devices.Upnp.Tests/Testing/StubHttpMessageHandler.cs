namespace Namotion.Devices.Upnp.Tests.Testing;

/// <summary>
/// An <see cref="HttpMessageHandler"/> that answers every request with what the given function returns, without a
/// real network call.
/// </summary>
internal sealed class StubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        respond(request);
}
