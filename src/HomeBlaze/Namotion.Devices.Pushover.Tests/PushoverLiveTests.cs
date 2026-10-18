using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Devices.Pushover.Tests.Testing;
using Namotion.Interceptor.Testing;
using Xunit;

namespace Namotion.Devices.Pushover.Tests;

/// <summary>
/// Runs against the real Pushover API with the tokens in PUSHOVER_APPLICATION_TOKEN and PUSHOVER_USER_KEY; without
/// them each test returns at once. The emergency test also needs PUSHOVER_RUN_EMERGENCY=1, because the phone alarms
/// until the notification is acknowledged on it or expires after 30 seconds.
/// </summary>
[Trait("Category", "Integration")]
public class PushoverLiveTests
{
    private static readonly string? ApplicationToken = Environment.GetEnvironmentVariable("PUSHOVER_APPLICATION_TOKEN");
    private static readonly string? UserKey = Environment.GetEnvironmentVariable("PUSHOVER_USER_KEY");
    private static readonly bool RunEmergency = Environment.GetEnvironmentVariable("PUSHOVER_RUN_EMERGENCY") == "1";

    private static bool HasTokens => !string.IsNullOrEmpty(ApplicationToken) && !string.IsNullOrEmpty(UserKey);

    private static PushoverService CreateService(string userKey)
    {
        var service = new PushoverService(new TestHttpClientFactory(new HttpClientHandler()), NullLogger<PushoverService>.Instance)
        {
            ApplicationToken = ApplicationToken
        };

        service.Targets = new Dictionary<string, PushoverTarget>
        {
            ["live"] = new() { Name = "Live test", UserKey = userKey }
        };

        return service;
    }

    [Fact]
    public async Task WhenUserKeyIsValid_ThenTargetRunsAndListsDevices()
    {
        if (!HasTokens)
        {
            return;
        }

        // Arrange
        var service = CreateService(UserKey!);

        // Act
        await using var running = await PushoverTestFactory.StartAsync(service);

        // Assert
        var target = service.Targets["live"];
        await AsyncTestHelpers.WaitUntilAsync(() => target.Status != ServiceStatus.Stopped);
        Assert.Equal(ServiceStatus.Running, target.Status);
        Assert.NotEmpty(target.Devices);
    }

    [Fact]
    public async Task WhenUserKeyIsInvalid_ThenTargetShowsTheError()
    {
        if (!HasTokens)
        {
            return;
        }

        // Arrange
        var service = CreateService("uQiRzpo4DXghDmr9QzzfQu27cmVRsG");

        // Act
        await using var running = await PushoverTestFactory.StartAsync(service);

        // Assert
        var target = service.Targets["live"];
        await AsyncTestHelpers.WaitUntilAsync(() => target.Status == ServiceStatus.Error);
        Assert.False(string.IsNullOrEmpty(target.StatusMessage));
    }

    [Fact]
    public async Task WhenSendingANormalNotification_ThenPushoverAcceptsIt()
    {
        if (!HasTokens)
        {
            return;
        }

        // Arrange
        var service = CreateService(UserKey!);
        var target = service.Targets["live"];

        // Act
        await target.SendNotificationAsync(new Notification
        {
            Message = "Live test of the Pushover integration.",
            Title = "Namotion test",
            Url = "https://github.com/RicoSuter/Namotion.Interceptor",
            UrlTitle = "Repository"
        }, CancellationToken.None);

        // Assert
        Assert.NotNull(target.LastSentAt);
    }

    [Fact]
    public async Task WhenSendingAnEmergencyNotification_ThenPushoverReturnsAReceipt()
    {
        if (!HasTokens || !RunEmergency)
        {
            return;
        }

        // Arrange
        var service = CreateService(UserKey!);
        var target = service.Targets["live"];
        target.EmergencyRetry = PushoverTarget.MinimumEmergencyRetry;
        target.EmergencyExpire = PushoverTarget.MinimumEmergencyRetry;

        // Act
        var result = await target.SendNotificationAsync(new Notification
        {
            Message = "Emergency test, it stops repeating after 30 seconds.",
            Title = "Namotion emergency test",
            Priority = NotificationPriority.Emergency
        }, CancellationToken.None);

        // Assert
        Assert.False(string.IsNullOrEmpty(result.Id));
    }
}
