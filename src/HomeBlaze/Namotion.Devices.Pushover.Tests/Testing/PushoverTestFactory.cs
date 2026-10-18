using Microsoft.Extensions.Logging.Abstractions;

namespace Namotion.Devices.Pushover.Tests.Testing;

internal static class PushoverTestFactory
{
    public static (PushoverService Service, FakePushoverHandler Handler) CreateService(params string[] targetKeys)
    {
        var handler = new FakePushoverHandler();
        var service = new PushoverService(new TestHttpClientFactory(handler), NullLogger<PushoverService>.Instance)
        {
            ApplicationToken = "app-token",
            RetryInterval = TimeSpan.FromMilliseconds(50)
        };

        service.Targets = targetKeys.ToDictionary(
            key => key,
            key => new PushoverTarget { Name = key, UserKey = $"user-{key}" });

        return (service, handler);
    }

    /// <summary>
    /// Starts the service and returns a handle that stops it again on dispose, replacing a try/finally around
    /// <c>StartAsync</c>/<c>StopAsync</c> in each test.
    /// </summary>
    public static async Task<IAsyncDisposable> StartAsync(PushoverService service)
    {
        await service.StartAsync(CancellationToken.None);
        return new RunningService(service);
    }

    private sealed class RunningService(PushoverService service) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => new(service.StopAsync(CancellationToken.None));
    }
}
