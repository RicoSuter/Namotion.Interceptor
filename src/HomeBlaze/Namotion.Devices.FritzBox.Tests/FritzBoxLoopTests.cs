using System.Reflection;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Devices;
using HomeBlaze.Storage.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Devices.FritzBox.Client;
using Namotion.Devices.FritzBox.Parsing;
using Namotion.Devices.FritzBox.Tests.Testing;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Attributes;
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
    public async Task WhenTheRouterIsConnected_ThenAResultWithItsUdnMatchesItAtAnotherAddress()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        var router = CreateRouter(fake);
        var sameRouter = new FritzBoxDiscoveryResult("fritz.box", null, null, "UUID:75802409-BCCB-40E7-8E6C-0123456789AB", IsRepeater: false);
        var otherRouter = sameRouter with { Udn = "uuid:other" };
        Assert.False(sameRouter.Matches(router));

        // Act
        await using var running = await RunAsync(router);
        await AsyncTestHelpers.WaitUntilAsync(() => router.IsConnected, message: "The router should connect.");

        // Assert
        Assert.True(sameRouter.Matches(router));
        Assert.False(otherRouter.Matches(router));
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
    public async Task WhenTheHostAddressIsInvalid_ThenTheLoopReportsItWithoutConnecting()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        var router = CreateRouter(fake);
        router.HostAddress = "fe80::1%@127.0.0.1?";

        // Act
        await using var running = await RunAsync(router);
        await WaitForErrorAsync(router, FritzBoxConnectionCheck.InvalidHostAddressMessage);

        // Assert
        Assert.False(router.IsConnected);
        Assert.Equal(0, fake.SecurityPortRequests);
        Assert.Equal(0, fake.SecureRequests);
    }

    [Fact]
    public async Task WhenNoPasswordIsConfigured_ThenTheRouterIsNotContactedUntilTheConfigurationChanges()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        var logger = new RecordingLogger<FritzBox>();
        var router = CreateRouter(fake, logger: logger);
        router.Password = null;

        // Act
        await using var running = await RunAsync(router);
        await WaitForErrorAsync(router, "No password configured");

        // Assert
        Assert.Equal(0, fake.SecurityPortRequests);
        Assert.Equal(0, fake.SecureRequests);
        Assert.DoesNotContain(logger.Warnings, warning => warning.Contains("refused the login", StringComparison.Ordinal));

        // Act
        router.Password = fake.Password;
        await router.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => router.IsConnected, message: "The router should connect once a password is configured.");
        Assert.Equal(0, fake.FailedLogins);
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
    public async Task WhenThePasswordChangesDuringASession_ThenOnePollFailsOneLoginAndPollingStopsUntilTheConfigurationChanges()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        var router = CreateRouter(fake);
        await using var running = await RunAsync(router);
        await AsyncTestHelpers.WaitUntilAsync(() => router.IsConnected, message: "The router should connect.");
        var loginCheck = fake.HoldNext($"{Tr64Services.DeviceInfo}#GetInfo");
        try
        {
            await loginCheck.Taken.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var queriesBeforeTheChange = fake.Actions.Count;

            // Act
            fake.Password = "changed";
            loginCheck.Released.TrySetResult();
            await WaitForErrorAsync(router, LoginFailedMessage);

            // Assert
            Assert.False(router.IsConnected);
            Assert.Equal(1, fake.FailedLogins);
            Assert.Equal(queriesBeforeTheChange, fake.Actions.Count);

            // Act
            router.Password = fake.Password;
            await router.ApplyConfigurationAsync(CancellationToken.None);

            // Assert
            // The login backoff is not timed here; that this is only the second connection proves that the router
            // did not reconnect on its own before the configuration changed.
            await AsyncTestHelpers.WaitUntilAsync(() => router.IsConnected, message: "The router should reconnect after the configuration change.");
            Assert.Equal(2, fake.SecurityPortRequests);
            Assert.Equal(1, fake.FailedLogins);
        }
        finally
        {
            loginCheck.Released.TrySetResult();
        }
    }

    [Fact]
    public async Task WhenTheHostListIsRefusedDuringASession_ThenPollingStopsUntilTheConfigurationChanges()
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

            // A 401 on the host list after a successful login is how the router answers once the user lost the
            // permission, so the login itself never fails and FailedLogins stays 0.
            fake.HttpErrors[HostListAction] = 401;
            var hostListQueries = CountActions(fake, HostListAction);

            // Act
            hostListQuery.Released.TrySetResult();
            await WaitForErrorAsync(router, LoginFailedMessage);

            // Assert
            // The held query is counted once it is released; no further one follows it.
            Assert.False(router.IsConnected);
            Assert.Equal(hostListQueries + 1, CountActions(fake, HostListAction));
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
        Assert.Equal(fake.PublicKeyPin, router.PublicKeyPin);
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
        Assert.Equal(fake.PublicKeyPin, router.PublicKeyPin);
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
        Assert.Equal(fake.PublicKeyPin, router.PublicKeyPin);
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
            () => router.PresentedPublicKeyPin is not null && router.Status == ServiceStatus.Error,
            message: "The router should refuse the key.");

        // Assert
        Assert.Equal(fake.PublicKeyPin, router.PresentedPublicKeyPin);
        Assert.Equal(CertificateInfo.Create(fake.Certificate).Fingerprint, router.PresentedCertificateFingerprint);
        Assert.Equal(otherPin, router.PublicKeyPin);
        Assert.Empty(fake.Actions);
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
    public async Task WhenTheGuestWlanIsToggled_ThenItSwitchesToTheOtherState()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        var router = CreateRouter(fake);
        await using var running = await RunAsync(router);
        var guest = await WaitForGuestWlanAsync(router);
        await AsyncTestHelpers.WaitUntilAsync(() => guest.IsOn is not null, message: "The router should report the guest WLAN state.");
        var wasOn = guest.IsOn!.Value;

        // Act
        await guest.ToggleAsync(CancellationToken.None);

        // Assert
        Assert.Equal(!wasOn, fake.IsGuestWlanEnabled);
        Assert.Equal(!wasOn, guest.IsOn);
    }

    [Theory]
    [InlineData(nameof(ISwitchController.TurnOnAsync), true)]
    [InlineData(nameof(ISwitchController.ToggleAsync), true)]
    [InlineData(nameof(ISwitchController.TurnOffAsync), false)]
    public void WhenTheGuestWlanIsSwitchedThroughTheSwitchController_ThenOnlyTurningItOffRunsWithoutConfirmation(string methodName, bool expected)
    {
        // Arrange
        var map = typeof(FritzBoxGuestWlan).GetInterfaceMap(typeof(ISwitchController));

        // Act
        var implementation = map.TargetMethods[Array.FindIndex(map.InterfaceMethods, method => method.Name == methodName)];

        // Assert
        Assert.Equal(typeof(FritzBoxGuestWlan), implementation.DeclaringType);
        Assert.Equal(expected, implementation.GetCustomAttribute<OperationAttribute>()?.RequiresConfirmation ?? false);
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

    [Fact]
    public async Task WhenTheInternetAccessOfAClientIsBlocked_ThenTheRouterIsToldAndTheStateFollows()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        var router = CreateRouter(fake);
        await using var running = await RunAsync(router);
        var laptop = await WaitForClientAsync(router, Snapshots.Laptop);

        // Act
        await laptop.BlockInternetAccessAsync(CancellationToken.None);

        // Assert
        Assert.True(fake.InternetAccessBlocks["192.168.178.20"]);
        Assert.True(laptop.IsInternetAccessBlocked);
        Assert.False(laptop.HasInternetAccess);
        Assert.Contains("DisallowWANAccessByIP", fake.Actions);
    }

    [Fact]
    public async Task WhenTheInternetAccessOfABlockedClientIsAllowed_ThenTheBlockIsLifted()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        fake.InternetAccessBlocks["192.168.178.20"] = true;
        var router = CreateRouter(fake);
        await using var running = await RunAsync(router);
        var laptop = await WaitForClientAsync(router, Snapshots.Laptop);
        await AsyncTestHelpers.WaitUntilAsync(() => laptop.IsInternetAccessBlocked == true, message: "The router should report the block.");

        // Act
        await laptop.AllowInternetAccessAsync(CancellationToken.None);

        // Assert
        Assert.False(fake.InternetAccessBlocks["192.168.178.20"]);
        Assert.False(laptop.IsInternetAccessBlocked);
        Assert.True(laptop.HasInternetAccess);
    }

    [Theory]
    [InlineData(nameof(FritzBoxClient.BlockInternetAccessAsync))]
    [InlineData(nameof(FritzBoxClient.AllowInternetAccessAsync))]
    public void WhenTheInternetAccessOfAClientIsChanged_ThenItRequiresConfirmation(string methodName)
    {
        // Act
        var attribute = typeof(FritzBoxClient).GetMethod(methodName)!.GetCustomAttribute<OperationAttribute>();

        // Assert
        Assert.True(attribute?.RequiresConfirmation);
    }

    [Theory]
    [InlineData(nameof(FritzBoxClient.BlockInternetAccessAsync), nameof(FritzBoxClient.BlockInternetAccess_IsEnabled))]
    [InlineData(nameof(FritzBoxClient.AllowInternetAccessAsync), nameof(FritzBoxClient.AllowInternetAccess_IsEnabled))]
    public void WhenAnInternetAccessOperationIsRegistered_ThenItsEnabledFlagNamesIt(string methodName, string flagName)
    {
        // Arrange
        var operationName = methodName[..^"Async".Length];

        // Act
        var attribute = typeof(FritzBoxClient).GetProperty(flagName)!.GetCustomAttribute<PropertyAttributeAttribute>();

        // Assert
        Assert.NotNull(attribute);
        Assert.Equal(operationName, attribute.PropertyName);
        Assert.Equal(KnownAttributes.IsEnabled, attribute.AttributeName);
    }

    [Fact]
    public void WhenARouterIsCreated_ThenInternetAccessControlIsAvailableOnlyInDebugBuilds()
    {
        // Act
        using var router = new FritzBox(NullLogger<FritzBox>.Instance);

        // Assert
#if DEBUG
        Assert.True(router.IsInternetAccessControlAvailable);
#else
        Assert.False(router.IsInternetAccessControlAvailable);
#endif
    }

    [Fact]
    public async Task WhenInternetAccessControlIsUnavailable_ThenTheOperationsAreDisabledAndBlockingSendsNothing()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        var router = CreateRouter(fake, isInternetAccessControlAvailable: false);
        await using var running = await RunAsync(router);
        var laptop = await WaitForClientAsync(router, Snapshots.Laptop);

        // Act & Assert
        Assert.False(laptop.BlockInternetAccess_IsEnabled);
        Assert.False(laptop.AllowInternetAccess_IsEnabled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => laptop.BlockInternetAccessAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => laptop.AllowInternetAccessAsync(CancellationToken.None));
        Assert.DoesNotContain("GetSpecificHostEntry", fake.Actions);
        Assert.DoesNotContain("DisallowWANAccessByIP", fake.Actions);
    }

    [Fact]
    public async Task WhenTheInternetAccessIsBlockedWithoutAConnection_ThenItThrows()
    {
        // Arrange
        using var router = new FritzBox(NullLogger<FritzBox>.Instance) { IsInternetAccessControlAvailable = true };
        var client = new FritzBoxClient(router, Snapshots.Laptop);
        client.Update(new HostEntry(Snapshots.Laptop, "192.168.178.20", true, "laptop", "802.11", 0, 144, false), null, null);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.BlockInternetAccessAsync(CancellationToken.None));
        Assert.Contains("not connected", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenTheIpAddressOfAClientChangedSinceThePoll_ThenTheBlockUsesTheAddressTheRouterListsNow()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        var router = CreateRouter(fake);
        await using var running = await RunAsync(router);
        var laptop = await WaitForClientAsync(router, Snapshots.Laptop);
        await AsyncTestHelpers.WaitUntilAsync(() => laptop.IpAddress == "192.168.178.20", message: "The router should report the address.");
        fake.HostListXml = fake.HostListXml.Replace("<IPAddress>192.168.178.20</IPAddress>", "<IPAddress>192.168.178.40</IPAddress>", StringComparison.Ordinal);

        // Act
        await laptop.BlockInternetAccessAsync(CancellationToken.None);

        // Assert
        Assert.True(fake.InternetAccessBlocks["192.168.178.40"]);
        Assert.False(fake.InternetAccessBlocks.ContainsKey("192.168.178.20"));
    }

    [Fact]
    public async Task WhenTheRouterNoLongerListsTheClient_ThenBlockingItThrows()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        var router = CreateRouter(fake);
        await using var running = await RunAsync(router);
        var laptop = await WaitForClientAsync(router, Snapshots.Laptop);
        fake.HostListXml = fake.HostListXml.Replace("AA:AA:AA:00:00:01", "AA:AA:AA:00:00:99", StringComparison.Ordinal);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => laptop.BlockInternetAccessAsync(CancellationToken.None));
        Assert.Contains("no longer lists", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("DisallowWANAccessByIP", fake.Actions);
    }

    [Fact]
    public async Task WhenTheLastIpAddressOfAnAbsentClientNowBelongsToAnotherDevice_ThenBlockingItThrows()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        var router = CreateRouter(fake);
        await using var running = await RunAsync(router);
        var laptop = await WaitForClientAsync(router, Snapshots.Laptop);
        fake.HostListXml = fake.HostListXml
            .Replace("<MACAddress>AA:AA:AA:00:00:01</MACAddress><Active>1</Active>", "<MACAddress>AA:AA:AA:00:00:01</MACAddress><Active>0</Active>", StringComparison.Ordinal)
            .Replace(
                "<Item><Index>1</Index>",
                "<Item><Index>9</Index><IPAddress>192.168.178.20</IPAddress><MACAddress>CC:CC:CC:00:00:01</MACAddress><Active>1</Active></Item><Item><Index>1</Index>",
                StringComparison.Ordinal);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => laptop.BlockInternetAccessAsync(CancellationToken.None));
        Assert.Contains("another device", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("DisallowWANAccessByIP", fake.Actions);
    }

    [Fact]
    public async Task WhenTheRouterListsTheClientWithoutIpAddress_ThenBlockingItThrows()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        var router = CreateRouter(fake);
        await using var running = await RunAsync(router);
        var laptop = await WaitForClientAsync(router, Snapshots.Laptop);
        fake.HostListXml = fake.HostListXml.Replace("<IPAddress>192.168.178.20</IPAddress>", "<IPAddress></IPAddress>", StringComparison.Ordinal);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => laptop.BlockInternetAccessAsync(CancellationToken.None));
        Assert.Contains("no IP address", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("DisallowWANAccessByIP", fake.Actions);
    }

    [Fact]
    public async Task WhenTheRouterOffersNoHostFilter_ThenBlockingAClientThrows()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        var hostFilterService = fake.DescriptionXml.Split('\n').Single(line => line.Contains(Tr64Services.HostFilter, StringComparison.Ordinal));
        fake.DescriptionXml = fake.DescriptionXml.Replace(hostFilterService, string.Empty, StringComparison.Ordinal);
        var router = CreateRouter(fake);
        await using var running = await RunAsync(router);
        var laptop = await WaitForClientAsync(router, Snapshots.Laptop);

        // Act & Assert
        await Assert.ThrowsAsync<FritzBoxServiceUnavailableException>(() => laptop.BlockInternetAccessAsync(CancellationToken.None));
    }

    [Fact]
    public async Task WhenTheAccessCannotBeReadBackAfterABlock_ThenTheClientShowsTheRequestedBlock()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        fake.Faults["GetWANAccessByIP"] = 820;
        var router = CreateRouter(fake);
        await using var running = await RunAsync(router);
        var laptop = await WaitForClientAsync(router, Snapshots.Laptop);

        // Act
        await laptop.BlockInternetAccessAsync(CancellationToken.None);

        // Assert
        Assert.True(fake.InternetAccessBlocks["192.168.178.20"]);
        Assert.True(laptop.IsInternetAccessBlocked);
    }

    [Fact]
    public async Task WhenLinkingTheClientsToTheirDevicesFails_ThenThePollsStillSucceedAndTheFailureIsLoggedOnce()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        var logger = new RecordingLogger<FritzBox>();
        var router = CreateRouter(fake, logger: logger);
        var device = new TestNetworkDevice(Snapshots.Laptop);
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        context.AddService<ISubjectPathResolver>(new ThrowingPathResolver());
        ((IInterceptorSubject)router).Context.AddFallbackContext(context);
        ((IInterceptorSubject)device).Context.AddFallbackContext(context);

        // Act
        await using var running = await RunAsync(router);
        await AsyncTestHelpers.WaitUntilAsync(() => router.LastUpdated is not null, message: "The router should poll once.");
        var firstPoll = router.LastUpdated;
        await AsyncTestHelpers.WaitUntilAsync(() => router.LastUpdated > firstPoll, message: "The router should poll again.");

        // Assert
        Assert.True(router.IsConnected);
        Assert.Equal(ServiceStatus.Running, router.Status);
        Assert.Null(router.Clients[Snapshots.Laptop].DevicePath);
        Assert.Single(logger.Warnings, warning => warning.Contains("linking the clients", StringComparison.Ordinal));
    }

    private static FritzBox CreateRouter(
        FakeFritzBox fake, string? password = null, string? pin = null, ILogger<FritzBox>? logger = null, bool isInternetAccessControlAvailable = true) =>
        new(logger ?? NullLogger<FritzBox>.Instance)
        {
            HostAddress = "127.0.0.1",
            Username = fake.Username,
            Password = password ?? fake.Password,
            PublicKeyPin = pin,
            DescriptionPort = fake.HttpPort,
            MinimumInterval = TimeSpan.FromMilliseconds(50),
            PollingInterval = TimeSpan.FromMilliseconds(100),
            RetryInterval = TimeSpan.FromMilliseconds(100),
            IsInternetAccessControlAvailable = isInternetAccessControlAvailable
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

    private static async Task<FritzBoxClient> WaitForClientAsync(FritzBox router, string macAddress)
    {
        await AsyncTestHelpers.WaitUntilAsync(() => router.Clients.ContainsKey(macAddress), message: "The router should list the client.");
        return router.Clients[macAddress];
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

    private sealed class ThrowingPathResolver : ISubjectPathResolver
    {
        public string GetPath(IInterceptorSubject subject, PathStyle style) => throw new InvalidOperationException("The path cannot be resolved.");

        public IReadOnlyList<string> GetPaths(IInterceptorSubject subject, PathStyle style) => [GetPath(subject, style)];

        public IInterceptorSubject? ResolveSubject(string path, PathStyle style, IInterceptorSubject? relativeTo = null) => null;
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
