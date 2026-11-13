namespace Namotion.Devices.Ecowitt.Tests;

internal sealed class TestHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new();
}
