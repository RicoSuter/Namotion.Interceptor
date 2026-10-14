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
    private const string LoginFailedMessage = "Login failed";
    private const string UnsavedCertificateFragment = "could not be saved";

    [Fact]
    public async Task WhenTheRouterAnswers_ThenTheSubjectRunsWithClientsAccessPointsAndWlans()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        var router = CreateRouter(fake);

        try
        {
            // Act
            await router.StartAsync(CancellationToken.None);
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
        finally
        {
            await StopAsync(router);
        }
    }

    [Fact]
    public async Task WhenThePasswordIsWrong_ThenOneLoginFailsAndNothingElseIsAsked()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        var router = CreateRouter(fake, password: "wrong");

        try
        {
            // Act
            await router.StartAsync(CancellationToken.None);
            await WaitForErrorAsync(router, LoginFailedMessage);

            // Assert
            Assert.Equal(ServiceStatus.Error, router.Status);
            Assert.False(router.IsConnected);
            Assert.Equal(1, fake.FailedLogins);
            Assert.DoesNotContain(HostListAction, fake.Actions);
        }
        finally
        {
            await StopAsync(router);
        }
    }

    [Fact]
    public async Task WhenNoPasswordIsConfigured_ThenNoLoginIsAttempted()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        var router = CreateRouter(fake);
        router.Password = null;

        try
        {
            // Act
            await router.StartAsync(CancellationToken.None);
            await WaitForErrorAsync(router, "No password configured");

            // Assert
            Assert.Equal(ServiceStatus.Error, router.Status);
            Assert.Equal(0, fake.FailedLogins);
            Assert.Equal(0, fake.SecureRequests);
        }
        finally
        {
            await StopAsync(router);
        }
    }

    [Fact]
    public async Task WhenTheLoginFailedAndThePasswordIsCorrected_ThenTheRouterConnectsWithoutWaitingForTheBackoff()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        var router = CreateRouter(fake, password: "wrong");

        try
        {
            await router.StartAsync(CancellationToken.None);
            await WaitForErrorAsync(router, LoginFailedMessage);

            // Act
            router.Password = fake.Password;
            await router.ApplyConfigurationAsync(CancellationToken.None);

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(() => router.IsConnected, message: "The router should connect after the configuration change.");
            Assert.Equal(1, fake.FailedLogins);
        }
        finally
        {
            await StopAsync(router);
        }
    }

    [Fact]
    public async Task WhenThePasswordChangesDuringASession_ThenPollingStopsUntilTheConfigurationChanges()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        var router = CreateRouter(fake);
        FakeFritzBox.Hold? hostListQuery = null;

        try
        {
            await router.StartAsync(CancellationToken.None);
            await AsyncTestHelpers.WaitUntilAsync(() => router.IsConnected, message: "The router should connect.");
            hostListQuery = fake.HoldNext($"{Tr64Services.Hosts}#{HostListAction}");
            await hostListQuery.Taken.Task.WaitAsync(TimeSpan.FromSeconds(30));

            // Act
            fake.HttpErrors[HostListAction] = 401;
            var hostListQueries = CountActions(fake, HostListAction);
            hostListQuery.Released.TrySetResult();
            await WaitForErrorAsync(router, LoginFailedMessage);

            // Assert
            Assert.Equal(ServiceStatus.Error, router.Status);
            Assert.False(router.IsConnected);
            Assert.Equal(hostListQueries, CountActions(fake, HostListAction));
            Assert.Equal(0, fake.FailedLogins);

            fake.HttpErrors.TryRemove(HostListAction, out _);
            await router.ApplyConfigurationAsync(CancellationToken.None);
            await AsyncTestHelpers.WaitUntilAsync(() => router.IsConnected, message: "The router should reconnect after the configuration change.");
            Assert.Equal(2, fake.SecurityPortRequests);
        }
        finally
        {
            hostListQuery?.Released.TrySetResult();
            await StopAsync(router);
        }
    }

    [Fact]
    public async Task WhenNoPinIsConfiguredAndNothingCanSaveIt_ThenThePresentedKeyIsTrustedWithAWarning()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        var router = CreateRouter(fake);

        try
        {
            // Act
            await router.StartAsync(CancellationToken.None);
            await AsyncTestHelpers.WaitUntilAsync(() => router.IsConnected, message: "The router should connect.");

            // Assert
            Assert.Equal(CertificateInfo.Create(fake.Certificate).Pin, router.CertificatePin);
            Assert.Equal(ServiceStatus.Running, router.Status);
            Assert.Contains(UnsavedCertificateFragment, router.StatusMessage);
        }
        finally
        {
            await StopAsync(router);
        }
    }

    [Fact]
    public async Task WhenNoPinIsConfiguredAndAWriterExists_ThenThePresentedKeyIsSaved()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        var router = CreateRouter(fake);
        var writer = new TestConfigurationWriter(InterceptorSubjectContext.Create().WithParents()) { Router = router };

        try
        {
            // Act
            await router.StartAsync(CancellationToken.None);
            await AsyncTestHelpers.WaitUntilAsync(() => router.IsConnected, message: "The router should connect.");

            // Assert
            Assert.Equal(CertificateInfo.Create(fake.Certificate).Pin, router.CertificatePin);
            Assert.Equal(1, writer.WriteCount);
            Assert.Null(router.StatusMessage);
        }
        finally
        {
            await StopAsync(router);
        }
    }

    [Fact]
    public async Task WhenAnUnsavedKeyIsSavedLater_ThenTheWarningClears()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        var router = CreateRouter(fake);

        // Keeps the loop idle between polls while the writer is attached.
        router.PollingInterval = TimeSpan.FromHours(1);

        try
        {
            await router.StartAsync(CancellationToken.None);
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
            Assert.Equal(CertificateInfo.Create(fake.Certificate).Pin, router.CertificatePin);
            Assert.Equal(ServiceStatus.Running, router.Status);
        }
        finally
        {
            await StopAsync(router);
        }
    }

    [Fact]
    public async Task WhenTheRouterPresentsAnotherKey_ThenTheConnectionIsRefusedAndTheKeyIsShown()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        using var other = FakeFritzBox.CreateCertificate();
        var router = CreateRouter(fake, pin: CertificateInfo.Create(other).Pin);

        try
        {
            // Act
            await router.StartAsync(CancellationToken.None);
            await AsyncTestHelpers.WaitUntilAsync(
                () => router.PresentedCertificatePin is not null && router.Status == ServiceStatus.Error,
                message: "The router should refuse the key.");

            // Assert
            Assert.Equal(CertificateInfo.Create(fake.Certificate).Pin, router.PresentedCertificatePin);
            Assert.Equal(CertificateInfo.Create(other).Pin, router.CertificatePin);
            Assert.Empty(fake.Actions);
        }
        finally
        {
            await StopAsync(router);
        }
    }

    [Fact]
    public async Task WhenAnOptionalActionIsRefused_ThenOnlyItsValuesAreNull()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        fake.Faults["X_AVM-DE_GetOnlineMonitor"] = 606;
        var router = CreateRouter(fake);

        try
        {
            // Act
            await router.StartAsync(CancellationToken.None);
            await AsyncTestHelpers.WaitUntilAsync(() => router.IsConnected && router.Clients.Count == 3, message: "The router should connect.");

            // Assert
            Assert.Null(router.UploadRate);
            Assert.Null(router.DownloadRate);
            Assert.Equal(40m, router.MaximumUploadRate);
            Assert.Equal(ServiceStatus.Running, router.Status);
        }
        finally
        {
            await StopAsync(router);
        }
    }

    [Fact]
    public async Task WhenThreePollsInARowFail_ThenTheConnectionIsReset()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        fake.Faults[HostListAction] = 501;
        var router = CreateRouter(fake);

        try
        {
            // Act
            await router.StartAsync(CancellationToken.None);

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(() => fake.SecurityPortRequests >= 2, message: "The router should reconnect after three failed polls.");
            Assert.True(CountActions(fake, HostListAction) >= 3);
            Assert.Null(router.LastUpdated);
        }
        finally
        {
            await StopAsync(router);
        }
    }

    [Fact]
    public async Task WhenTheFirmwareHasNoHostList_ThenTheStatusSaysToUpdate()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        fake.Faults[HostListAction] = 401;
        var router = CreateRouter(fake);

        try
        {
            // Act
            await router.StartAsync(CancellationToken.None);
            await WaitForErrorAsync(router, "FRITZ!OS");

            // Assert
            Assert.Equal(ServiceStatus.Error, router.Status);
            Assert.Equal(1, fake.SecurityPortRequests);
        }
        finally
        {
            await StopAsync(router);
        }
    }

    [Fact]
    public async Task WhenTheGuestWlanIsSwitchedOn_ThenTheRouterIsToldAndTheStateFollows()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        var router = CreateRouter(fake);

        try
        {
            await router.StartAsync(CancellationToken.None);
            await AsyncTestHelpers.WaitUntilAsync(() => router.Wlans.ContainsKey(FritzBoxWlanRoles.Guest), message: "The router should list its guest WLAN.");
            var guest = (FritzBoxGuestWlan)router.Wlans[FritzBoxWlanRoles.Guest];

            // Act
            await guest.TurnOnAsync(CancellationToken.None);

            // Assert
            Assert.True(fake.IsGuestWlanEnabled);
            Assert.True(guest.IsOn);
            Assert.Contains("SetEnable", fake.Actions);
        }
        finally
        {
            await StopAsync(router);
        }
    }

    [Fact]
    public async Task WhenAPollReadTheOldStateBeforeASwitch_ThenItDoesNotWriteItBack()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        var router = CreateRouter(fake);
        FakeFritzBox.Hold? staleRead = null;
        FakeFritzBox.Hold? nextRead = null;

        try
        {
            await router.StartAsync(CancellationToken.None);
            await AsyncTestHelpers.WaitUntilAsync(() => router.Wlans.ContainsKey(FritzBoxWlanRoles.Guest), message: "The router should list its guest WLAN.");
            var guest = (FritzBoxGuestWlan)router.Wlans[FritzBoxWlanRoles.Guest];
            staleRead = fake.HoldNextGuestWlanInfo();
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
            staleRead?.Released.TrySetResult();
            nextRead?.Released.TrySetResult();
            await StopAsync(router);
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
    /// Waits for the error status with a message containing <paramref name="messageFragment"/>; the message is
    /// written after the status.
    /// </summary>
    private static Task WaitForErrorAsync(FritzBox router, string messageFragment) =>
        AsyncTestHelpers.WaitUntilAsync(
            () => router.Status == ServiceStatus.Error && router.StatusMessage?.Contains(messageFragment, StringComparison.Ordinal) == true,
            message: $"The router should report an error containing '{messageFragment}'.");

    private static int CountActions(FakeFritzBox fake, string action) =>
        fake.Actions.Count(received => received == action);

    private static async Task StopAsync(FritzBox router)
    {
        await router.StopAsync(CancellationToken.None);
        router.Dispose();
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
