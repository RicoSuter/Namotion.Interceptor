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
            ReceiptPollingInterval = TimeSpan.FromMilliseconds(50),
            MinimumReceiptPollingInterval = TimeSpan.FromMilliseconds(10),
            RetryInterval = TimeSpan.FromMilliseconds(50)
        };

        service.Targets = targetKeys.ToDictionary(
            key => key,
            key => new PushoverTarget { Name = key, UserKey = $"user-{key}" });

        return (service, handler);
    }
}
