using HomeBlaze.Abstractions;
using HomeBlaze.Storage.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Devices.FritzBox.Client;
using Namotion.Devices.FritzBox.Tests.Testing;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Xunit;

namespace Namotion.Devices.FritzBox.Tests;

public class FritzBoxLoopTests
{
    private const string HostListAction = "X_AVM-DE_GetHostListPath";
    private const string OnlineMonitorAction = "X_AVM-DE_GetOnlineMonitor";
    private const string LoginFailedMessage = "Login failed";
    private const string UnsavedCertificateFragment = "could not be saved";

    [Fact]
    public async Task WhenTheRouterAnswers_ThenTheSubjectRunsWithClientsAccessPointsAndWlans()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        var router = CreateRouter(fake);

        // Act
        await using var running = await RunAsync(router);
        await AsyncTestHelpers.WaitUntilAsync(
            () => router.IsConnected && router.LastUpdated is not null && router.Clients.Count == 3,
            message: "The router should connect and list its clients.");

        // Assert
        Assert.Equal(ServiceStatus.Running, router.Status);
        Assert.Equal(2, router.AccessPoints.Count);
        Assert.Equal(3, router.Wlans.Count);
        Assert.Equal("203.0.113.7", router.ExternalIpAddress);
        Assert.Equal("FRITZ!Box 7590", router.Model);
    }

    [Fact]
    public async Task WhenThePasswordIsWrong_ThenOneLoginFailsAndNothingElseIsAsked()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        var router = CreateRouter(fake, password: "wrong");

        // Act
        await using var running = await RunAsync(router);
        await WaitForErrorAsync(router, LoginFailedMessage);

        // Assert
        Assert.False(router.IsConnected);
        Assert.Equal(1, fake.FailedLogins);
        Assert.DoesNotContain(HostListAction, fake.Actions);
    }

    [Fact]
    public async Task WhenNoPasswordIsConfigured_ThenNoLoginIsAttempted()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        var router = CreateRouter(fake);
        router.Password = null;

        // Act
        await using var running = await RunAsync(router);
        await WaitForErrorAsync(router, "No password configured");

        // Assert
        Assert.Equal(0, fake.FailedLogins);
        Assert.Equal(0, fake.SecureRequests);
    }

    [Fact]
    public async Task WhenTheLoginFailedAndThePasswordIsCorrected_ThenTheRouterConnectsWithoutWaitingForTheBackoff()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        var router = CreateRouter(fake, password: "wrong");
        await using var running = await RunAsync(router);
        await WaitForErrorAsync(router, LoginFailedMessage);

        // Act
        router.Password = fake.Password;
        await router.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => router.IsConnected, message: "The router should connect after the configuration change.");
        Assert.Equal(1, fake.FailedLogins);
    }

    [Fact]
    public async Task WhenThePasswordChangesDuringASession_ThenPollingStopsUntilTheConfigurationChanges()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        var router = CreateRouter(fake);
        await using var running = await RunAsync(router);
        await AsyncTestHelpers.WaitUntilAsync(() => router.IsConnected, message: "The router should connect.");
        var hostListQuery = fake.HoldNext($"{Tr64Services.Hosts}#{HostListAction}");
        try
        {
            await hostListQuery.Taken.Task.WaitAsync(TimeSpan.FromSeconds(30));

            // A 401 on the host list after a successful login is how the router answers once the password changed,
            // so the login itself never fails and FailedLogins stays 0.
            fake.HttpErrors[HostListAction] = 401;
            var hostListQueries = CountActions(fake, HostListAction);

            // Act
            hostListQuery.Released.TrySetResult();
            await WaitForErrorAsync(router, LoginFailedMessage);

            // Assert
            Assert.False(router.IsConnected);
            Assert.Equal(hostListQueries, CountActions(fake, HostListAction));
            Assert.Equal(0, fake.FailedLogins);

            // Act
            fake.HttpErrors.TryRemove(HostListAction, out _);
            await router.ApplyConfigurationAsync(CancellationToken.None);

            // Assert
            // The login backoff is not timed here; that this is only the second connection proves that the router
            // did not reconnect on its own before the configuration changed.
            await AsyncTestHelpers.WaitUntilAsync(() => router.IsConnected, message: "The router should reconnect after the configuration change.");
            Assert.Equal(2, fake.SecurityPortRequests);
        }
        finally
        {
            hostListQuery.Released.TrySetResult();
        }
    }

    [Fact]
    public async Task WhenNoPinIsConfiguredAndNothingCanSaveIt_ThenThePresentedKeyIsTrustedWithAWarning()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        var router = CreateRouter(fake);

        // Act
        await using var running = await RunAsync(router);
        await AsyncTestHelpers.WaitUntilAsync(() => router.IsConnected, message: "The router should connect.");

        // Assert
        Assert.Equal(fake.CertificatePin, router.CertificatePin);
        Assert.Equal(ServiceStatus.Running, router.Status);
        Assert.Contains(UnsavedCertificateFragment, router.StatusMessage);
    }

    [Fact]
    public async Task WhenNoPinIsConfiguredAndAWriterExists_ThenThePresentedKeyIsSaved()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        var router = CreateRouter(fake);
        var writer = new TestConfigurationWriter(InterceptorSubjectContext.Create().WithParents()) { Router = router };

        // Act
        await using var running = await RunAsync(router);
        await AsyncTestHelpers.WaitUntilAsync(() => router.IsConnected, message: "The router should connect.");

        // Assert
        Assert.Equal(fake.CertificatePin, router.CertificatePin);
        Assert.Equal(1, writer.WriteCount);
        Assert.Null(router.StatusMessage);
    }

    [Fact]
    public async Task WhenAnUnsavedKeyIsSavedLater_ThenTheWarningClears()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        var router = CreateRouter(fake);

        // Keeps the loop idle between polls while the writer is attached.
        router.PollingInterval = TimeSpan.FromHours(1);

        await using var running = await RunAsync(router);
        await AsyncTestHelpers.WaitUntilAsync(
            () => router.IsConnected && router.StatusMessage?.Contains(UnsavedCertificateFragment) == true,
            message: "The router should connect with the unsaved key warning.");

        // Act
        var writer = new TestConfigurationWriter(InterceptorSubjectContext.Create().WithParents()) { Router = router };
        await router.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => writer.WriteCount == 1 && router.IsConnected && router.StatusMessage is null,
            message: "The router should save the key and clear the warning after reconnecting.");
        Assert.Equal(fake.CertificatePin, router.CertificatePin);
        Assert.Equal(ServiceStatus.Running, router.Status);
    }

    [Fact]
    public async Task WhenTheRouterPresentsAnotherKey_ThenTheConnectionIsRefusedAndTheKeyIsShown()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        using var other = FakeFritzBox.CreateCertificate();
        var otherPin = CertificateInfo.Create(other).Pin;
        var router = CreateRouter(fake, pin: otherPin);

        // Act
        await using var running = await RunAsync(router);
        await AsyncTestHelpers.WaitUntilAsync(
            () => router.PresentedCertificatePin is not null && router.Status == ServiceStatus.Error,
            message: "The router should refuse the key.");

        // Assert
        Assert.Equal(fake.CertificatePin, router.PresentedCertificatePin);
        Assert.Equal(otherPin, router.CertificatePin);
        Assert.Empty(fake.Actions);
    }

    [Fact]
    public async Task WhenAnOptionalActionIsRefused_ThenOnlyItsValuesAreNull()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        fake.Faults[OnlineMonitorAction] = 606;
        var router = CreateRouter(fake);

        // Act
        await using var running = await RunAsync(router);
        await AsyncTestHelpers.WaitUntilAsync(() => router.IsConnected && router.Clients.Count == 3, message: "The router should connect.");

        // Assert
        Assert.Null(router.UploadRate);
        Assert.Null(router.DownloadRate);
        Assert.Equal(40m, router.MaximumUploadRate);
        Assert.Equal(ServiceStatus.Running, router.Status);
    }

    [Fact]
    public async Task WhenThreePollsInARowFail_ThenTheConnectionIsReset()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        fake.Faults[HostListAction] = 501;
        var router = CreateRouter(fake);

        // Act
        await using var running = await RunAsync(router);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => fake.SecurityPortRequests >= 2, message: "The router should reconnect after three failed polls.");
        Assert.True(CountActions(fake, HostListAction) >= 3);
        Assert.Null(router.LastUpdated);
    }

    [Fact]
    public async Task WhenTheFirmwareHasNoHostList_ThenTheStatusSaysToUpdate()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        fake.Faults[HostListAction] = 401;
        var router = CreateRouter(fake);

        // Act
        await using var running = await RunAsync(router);
        await WaitForErrorAsync(router, "FRITZ!OS");

        // Assert
        Assert.Equal(1, fake.SecurityPortRequests);
    }

    [Fact]
    public async Task WhenTheGuestWlanIsSwitchedOn_ThenTheRouterIsToldAndTheStateFollows()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        var router = CreateRouter(fake);
        await using var running = await RunAsync(router);
        var guest = await WaitForGuestWlanAsync(router);

        // Act
        await guest.TurnOnAsync(CancellationToken.None);

        // Assert
        Assert.True(fake.IsGuestWlanEnabled);
        Assert.True(guest.IsOn);
        Assert.Contains("SetEnable", fake.Actions);
    }

    [Fact]
    public async Task WhenAPollReadTheOldStateBeforeASwitch_ThenItDoesNotWriteItBack()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        var router = CreateRouter(fake);
        await using var running = await RunAsync(router);
        var guest = await WaitForGuestWlanAsync(router);
        var staleRead = fake.HoldNextGuestWlanInfo();
        FakeFritzBox.Hold? nextRead = null;
        try
        {
            await staleRead.Taken.Task.WaitAsync(TimeSpan.FromSeconds(30));

            // Act
            await guest.TurnOnAsync(CancellationToken.None);
            var lastUpdated = router.LastUpdated;

            // Holds the poll after the stale one before it can apply, so only the stale poll can write the state.
            nextRead = fake.HoldNextGuestWlanInfo();
            staleRead.Released.TrySetResult();
            await AsyncTestHelpers.WaitUntilAsync(() => router.LastUpdated != lastUpdated, message: "The held poll should complete.");

            // Assert
            Assert.True(guest.IsOn);
        }
        finally
        {
            staleRead.Released.TrySetResult();
            nextRead?.Released.TrySetResult();
        }
    }

    [Fact]
    public async Task WhenTheGuestWlanIsSwitchedWithoutAConnection_ThenItThrows()
    {
        // Arrange
        using var router = new FritzBox(NullLogger<FritzBox>.Instance);
        var guest = new FritzBoxGuestWlan(router, FakeFritzBox.GuestWlanServiceType);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => guest.TurnOnAsync(CancellationToken.None));
    }

    private static FritzBox CreateRouter(FakeFritzBox fake, string? password = null, string? pin = null) =>
        new(NullLogger<FritzBox>.Instance)
        {
            HostAddress = "127.0.0.1",
            Username = fake.Username,
            Password = password ?? fake.Password,
            CertificatePin = pin,
            DescriptionPort = fake.HttpPort,
            MinimumInterval = TimeSpan.FromMilliseconds(50),
            InitialLoginRetryDelay = TimeSpan.FromHours(1),
            PollingInterval = TimeSpan.FromMilliseconds(100),
            RetryInterval = TimeSpan.FromMilliseconds(100)
        };

    /// <summary>
    /// Starts the router; disposing the result stops and disposes it. Release held requests before that.
    /// </summary>
    private static async Task<RunningRouter> RunAsync(FritzBox router)
    {
        await router.StartAsync(CancellationToken.None);
        return new RunningRouter(router);
    }

    /// <summary>
    /// Waits for the error status with a message containing <paramref name="messageFragment"/>; the message is
    /// written after the status.
    /// </summary>
    private static Task WaitForErrorAsync(FritzBox router, string messageFragment) =>
        AsyncTestHelpers.WaitUntilAsync(
            () => router.Status == ServiceStatus.Error && router.StatusMessage?.Contains(messageFragment, StringComparison.Ordinal) == true,
            message: $"The router should report an error containing '{messageFragment}'.");

    private static async Task<FritzBoxGuestWlan> WaitForGuestWlanAsync(FritzBox router)
    {
        await AsyncTestHelpers.WaitUntilAsync(() => router.Wlans.ContainsKey(FritzBoxWlanRoles.Guest), message: "The router should list its guest WLAN.");
        return (FritzBoxGuestWlan)router.Wlans[FritzBoxWlanRoles.Guest];
    }

    private static int CountActions(FakeFritzBox fake, string action) =>
        fake.Actions.Count(received => received == action);

    private sealed class RunningRouter(FritzBox router) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await router.StopAsync(CancellationToken.None);
            router.Dispose();
        }
    }
}

[InterceptorSubject]
public partial class TestConfigurationWriter : IConfigurationWriter
{
    private int _writeCount;

    public int WriteCount => Volatile.Read(ref _writeCount);

    public partial FritzBox? Router { get; set; }

    public Task<bool> WriteConfigurationAsync(IInterceptorSubject subject, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _writeCount);
        return Task.FromResult(true);
    }
}
