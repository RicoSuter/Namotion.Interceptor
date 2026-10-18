using System.Net;
using HomeBlaze.Abstractions;
using Namotion.Devices.Pushover.Tests.Testing;
using Namotion.Interceptor.Testing;
using Xunit;

namespace Namotion.Devices.Pushover.Tests;

public class PushoverServiceMonitoringTests
{
    private const string ValidatePath = "/1/users/validate.json";

    [Fact]
    public async Task WhenStarted_ThenTargetsAreValidatedAndShowTheirDevices()
    {
        // Arrange
        var (service, handler) = PushoverTestFactory.CreateService("rico");
        handler.Respond = _ => (HttpStatusCode.OK, """{"status":1,"devices":["phone"]}""");

        // Act
        await using var running = await PushoverTestFactory.StartAsync(service);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => service.Status == ServiceStatus.Running);
        var target = service.Targets["rico"];
        Assert.Equal(ServiceStatus.Running, target.Status);
        Assert.Equal(["phone"], target.Devices);
        Assert.Null(target.StatusMessage);
    }

    [Fact]
    public async Task WhenUserKeyIsInvalid_ThenTargetStatusIsErrorAndServiceRuns()
    {
        // Arrange
        var (service, handler) = PushoverTestFactory.CreateService("rico");
        handler.Respond = _ => (HttpStatusCode.BadRequest, """{"user":"invalid","errors":["user key is invalid"],"status":0}""");

        // Act
        await using var running = await PushoverTestFactory.StartAsync(service);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => service.Targets["rico"].Status == ServiceStatus.Error);
        Assert.Equal("user key is invalid", service.Targets["rico"].StatusMessage);
        await AsyncTestHelpers.WaitUntilAsync(() => service.Status == ServiceStatus.Running);
    }

    [Fact]
    public async Task WhenValidationFailsTransiently_ThenItRetriesWithoutMarkingTheTargetFailed()
    {
        // Arrange
        var (service, handler) = PushoverTestFactory.CreateService("rico");
        var validationAttempts = 0;
        // The first attempt is a transient outage (503); the retry succeeds.
        handler.Respond = _ => Interlocked.Increment(ref validationAttempts) == 1
            ? (HttpStatusCode.ServiceUnavailable, "Service Unavailable")
            : (HttpStatusCode.OK, """{"status":1,"devices":["phone"]}""");

        // Act
        await using var running = await PushoverTestFactory.StartAsync(service);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => service.Targets["rico"].Status == ServiceStatus.Running);
        var target = service.Targets["rico"];
        Assert.Null(target.StatusMessage);
        Assert.Equal(["phone"], target.Devices);
        Assert.True(validationAttempts >= 2);
    }

    [Fact]
    public async Task WhenApplicationTokenIsInvalid_ThenServiceStatusIsError()
    {
        // Arrange
        var (service, handler) = PushoverTestFactory.CreateService("rico");
        handler.Respond = _ => (HttpStatusCode.BadRequest, """{"token":"invalid","errors":["application token is invalid"],"status":0}""");

        // Act
        await using var running = await PushoverTestFactory.StartAsync(service);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => service.Status == ServiceStatus.Error);
        Assert.Equal("The application token is invalid", service.StatusMessage);
    }

    [Fact]
    public async Task WhenTheApplicationTokenIsChangedAfterBeingRejected_ThenTheServiceRecoversToRunning()
    {
        // Arrange
        var (service, handler) = PushoverTestFactory.CreateService("rico");
        handler.Respond = request => request.Form["token"] == "app-token"
            ? (HttpStatusCode.BadRequest, """{"token":"invalid","errors":["application token is invalid"],"status":0}""")
            : (HttpStatusCode.OK, """{"status":1,"devices":["phone"]}""");

        await using var running = await PushoverTestFactory.StartAsync(service);
        await AsyncTestHelpers.WaitUntilAsync(() => service.Status == ServiceStatus.Error);

        // Act
        service.ApplicationToken = "new-app-token";

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => service.Status == ServiceStatus.Running);
        Assert.Equal(ServiceStatus.Running, service.Targets["rico"].Status);
    }

    [Fact]
    public async Task WhenNoApplicationTokenIsConfigured_ThenServiceIsStoppedWithoutRequests()
    {
        // Arrange
        var (service, handler) = PushoverTestFactory.CreateService("rico");
        service.ApplicationToken = null;

        // Act
        await using var running = await PushoverTestFactory.StartAsync(service);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => service.StatusMessage == "No application token configured");
        Assert.Equal(ServiceStatus.Stopped, service.Status);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task WhenTheApplicationTokenIsConfiguredAfterBeingMissing_ThenTheServiceRecoversToRunning()
    {
        // Arrange
        var (service, _) = PushoverTestFactory.CreateService("rico");
        service.ApplicationToken = null;

        await using var running = await PushoverTestFactory.StartAsync(service);
        await AsyncTestHelpers.WaitUntilAsync(() => service.StatusMessage == "No application token configured");

        // Act
        service.ApplicationToken = "app-token";
        await service.Targets["rico"].ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => service.Status == ServiceStatus.Running);
    }

    [Fact]
    public async Task WhenConfigurationIsApplied_ThenTargetsAreValidatedAgain()
    {
        // Arrange
        var (service, handler) = PushoverTestFactory.CreateService("rico");
        await using var running = await PushoverTestFactory.StartAsync(service);
        await AsyncTestHelpers.WaitUntilAsync(() => handler.RequestsTo(ValidatePath).Length == 1);

        // Act
        await service.Targets["rico"].ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => handler.RequestsTo(ValidatePath).Length == 2);
    }

    [Fact]
    public async Task WhenConfigurationIsAppliedDuringAValidation_ThenTheTargetsAreValidatedOnceMore()
    {
        // Arrange
        var (service, handler) = PushoverTestFactory.CreateService("rico");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.BeforeRespond = () => release.Task;
        await using var running = await PushoverTestFactory.StartAsync(service);
        await AsyncTestHelpers.WaitUntilAsync(() => handler.RequestsTo(ValidatePath).Length == 1);

        // Act
        await service.ApplyConfigurationAsync(CancellationToken.None);
        release.SetResult();

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => handler.RequestsTo(ValidatePath).Length == 2 && service.Status == ServiceStatus.Running);
        Assert.Equal(2, handler.RequestsTo(ValidatePath).Length);
    }
}
