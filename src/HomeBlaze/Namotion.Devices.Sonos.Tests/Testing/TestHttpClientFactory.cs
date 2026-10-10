namespace Namotion.Devices.Sonos.Tests.Testing;

internal sealed class TestHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new();
}
