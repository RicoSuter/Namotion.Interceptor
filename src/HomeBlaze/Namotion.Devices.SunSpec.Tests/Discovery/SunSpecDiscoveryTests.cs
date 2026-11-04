using System.Collections.Concurrent;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using FluentModbus;
using HomeBlaze.Abstractions;
using Namotion.Devices.SunSpec.Models;
using Namotion.Devices.SunSpec.Tests.Models;
using Namotion.Devices.SunSpec.Tests.Testing;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Devices.SunSpec.Tests.Discovery;

[Trait("Category", "Integration")]
[Collection(SunSpecIntegrationCollection.Name)]
public class SunSpecDiscoveryTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);

    private static SunSpecTestChain CreateChain(int markerAddress = 40000) => new SunSpecTestChain(markerAddress)
        .AddModel(1, new Dictionary<string, object?> { ["Mn"] = "Vendor", ["Md"] = "Model" })
        .AddModel(103, new Dictionary<string, object?> { ["W"] = 1500, ["W_SF"] = 0 });

    [Fact]
    public async Task WhenAConfiguredUnitHasNoMarker_ThenOnlyTheOtherUnitsAreDiscovered()
    {
        // Arrange: unit 1 has its marker at the second candidate address, so the read at 40000 is rejected first.
        using var server = new SunSpecTestServer(1, 2);
        server.Start((1, CreateChain(markerAddress: 50000)));
        var logger = new RecordingLogger<SunSpecDevice>();

        // Act
        await using var host = await HostedSunSpecDevice.StartAsync(server.Port, device => device.UnitIds = [1, 2], logger);
        var device = host.Device;

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => device.IsConnected && device.Units.GetValueOrDefault(1)?.Devices[0].Common?.Mn == "Vendor",
            WaitTimeout,
            message: "Unit 1 should be discovered and read.");
        Assert.Equal([1], device.Units.Keys);
        Assert.Equal(50000, device.Units[1].MarkerAddress);
        Assert.Contains(logger.Warnings, warning => warning.Contains("unit 2 has no \"SunS\" marker", StringComparison.Ordinal));
        Assert.Equal(ServiceStatus.Running, device.Status);
        Assert.Equal("Unit 2 not found", device.StatusMessage);
    }

    [Fact]
    public async Task WhenAMissingUnitGetsItsChain_ThenItIsDiscoveredWithoutAConfigurationChange()
    {
        // Arrange
        using var server = new SunSpecTestServer(1, 2);
        server.Start((1, CreateChain()));
        await using var host = await HostedSunSpecDevice.StartAsync(server.Port, device =>
        {
            device.UnitIds = [1, 2];
            device.MinimumRediscoveryInterval = TimeSpan.FromMilliseconds(500);
        });
        var device = host.Device;
        await AsyncTestHelpers.WaitUntilAsync(
            () => device.IsConnected && device.StatusMessage == "Unit 2 not found",
            WaitTimeout,
            message: "Unit 1 should be discovered without unit 2.");

        // Act
        server.WriteChain(CreateChain(), unitId: 2);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => device.IsConnected && device.Units.GetValueOrDefault(2)?.Devices[0].Common?.Mn == "Vendor",
            WaitTimeout,
            message: "Unit 2 should be discovered and read once it has a chain.");
        Assert.Equal([1, 2], device.Units.Keys.Order());
    }

    [Fact]
    public async Task WhenAPlannedRediscoveryRuns_ThenTheStatusStaysRunningAndTheMissingUnitIsWarnedOnce()
    {
        // Arrange
        using var server = new SunSpecTestServer(1, 2);
        server.Start((1, CreateChain()));
        var logger = new RecordingLogger<SunSpecDevice>();
        await using var host = await HostedSunSpecDevice.StartAsync(server.Port, device =>
        {
            device.UnitIds = [1, 2];
            device.MinimumRediscoveryInterval = TimeSpan.FromMilliseconds(200);
        }, logger);
        var device = host.Device;
        await AsyncTestHelpers.WaitUntilAsync(
            () => device.IsConnected && device.StatusMessage == "Unit 2 not found",
            WaitTimeout,
            message: "Unit 1 should be discovered without unit 2.");

        var discoveryCount = device.DiscoveryCount;
        var statusChanges = new ConcurrentQueue<string>();
        using var subscription = host.Context.GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Where(change => ReferenceEquals(change.Property.Subject, device) &&
                             change.Property.Name is nameof(SunSpecDevice.IsConnected) or nameof(SunSpecDevice.Status) or nameof(SunSpecDevice.StatusMessage))
            .Subscribe(change => statusChanges.Enqueue($"{change.Property.Name}: {change.GetNewValue<object?>()}"));

        // Act
        await AsyncTestHelpers.WaitUntilAsync(
            () => device.DiscoveryCount >= discoveryCount + 2,
            WaitTimeout,
            message: "Two planned rediscoveries should run.");

        // Assert
        Assert.Empty(statusChanges);
        Assert.Single(logger.Warnings, warning => warning.Contains("unit 2 has no \"SunS\" marker", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(ModbusExceptionCode.GatewayPathUnavailable)]
    [InlineData(ModbusExceptionCode.GatewayTargetDeviceFailedToRespond)]
    public async Task WhenAGatewayCannotReachAUnit_ThenOnlyTheOtherUnitsAreDiscovered(ModbusExceptionCode exceptionCode)
    {
        // Arrange
        using var server = new SunSpecTestServer(1, 2);
        server.SetUnitException(2, exceptionCode);
        server.Start((1, CreateChain()));
        var logger = new RecordingLogger<SunSpecDevice>();

        // Act
        await using var host = await HostedSunSpecDevice.StartAsync(server.Port, device => device.UnitIds = [1, 2], logger);
        var device = host.Device;

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => device.IsConnected && device.Units.GetValueOrDefault(1)?.Devices[0].Common?.Mn == "Vendor",
            WaitTimeout,
            message: "Unit 1 should be discovered and read.");
        Assert.Equal([1], device.Units.Keys);
        Assert.Contains(logger.Warnings, warning => warning.Contains("unit 2 cannot be reached through the gateway", StringComparison.Ordinal));
        Assert.Equal("Unit 2 not found", device.StatusMessage);
    }

    [Fact]
    public async Task WhenAUnitHasAMalformedChain_ThenItIsSkippedWithAnError()
    {
        // Arrange: the second model claims more registers than the device serves.
        using var server = new SunSpecTestServer(1, 2);
        server.Start((1, CreateChain()), (2, new SunSpecTestChain().AddModel(1).AddRawModel([64998, 50])));
        var logger = new RecordingLogger<SunSpecDevice>();

        // Act
        await using var host = await HostedSunSpecDevice.StartAsync(server.Port, device => device.UnitIds = [1, 2], logger);
        var device = host.Device;

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => device.IsConnected && device.Units.GetValueOrDefault(1)?.Devices[0].Common?.Mn == "Vendor",
            WaitTimeout,
            message: "Unit 1 should be discovered and read.");
        Assert.Equal([1], device.Units.Keys);
        Assert.Contains(logger.Errors, error => error.Contains("unit 2 has a malformed model chain", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhenAModelIsTooShortForAScaleFactor_ThenThePointsItScalesAreSkippedAndTheRestIsRead()
    {
        // Arrange: model 121 is 30 registers long; at 29 its last point, ECPNomHz_SF, is missing.
        var chain = new SunSpecTestChain()
            .AddModel(1, new Dictionary<string, object?> { ["Mn"] = "Vendor" })
            .AddModel(121, new Dictionary<string, object?> { ["WMax"] = 5000, ["WMax_SF"] = 0, ["ECPNomHz"] = 50 }, length: 29)
            .AddModel(103, new Dictionary<string, object?> { ["W"] = 1500, ["W_SF"] = 0 });
        using var server = new SunSpecTestServer();
        server.Start((1, chain));

        // Act
        await using var host = await HostedSunSpecDevice.StartAsync(server.Port);
        var device = host.Device;

        // Assert
        ISunSpecModel[] GetModels() => device.Units.GetValueOrDefault(1)?.Devices[0].Models ?? [];
        await AsyncTestHelpers.WaitUntilAsync(
            () => device.IsConnected &&
                  GetModels().OfType<SunSpecBasicSettings>().FirstOrDefault()?.WMax == 5000m &&
                  GetModels().OfType<SunSpecInverter>().FirstOrDefault()?.W == 1500m,
            WaitTimeout,
            message: "The shortened model and the model after it should be read.");
        var settings = GetModels().OfType<SunSpecBasicSettings>().Single();
        Assert.Null(settings.ECPNomHz);
        Assert.Null(settings.ECPNomHz_SF);
        Assert.Equal(ServiceStatus.Running, device.Status);
    }

    [Fact]
    public async Task WhenTheModelDefinitionsPathIsInvalid_ThenStatusIsErrorUntilTheConfigurationIsFixed()
    {
        // Arrange
        using var server = new SunSpecTestServer();
        server.Start((1, CreateChain()));
        await using var host = await HostedSunSpecDevice.StartAsync(server.Port, device => device.ModelDefinitionsPath = "invalid\0path");
        var device = host.Device;
        await AsyncTestHelpers.WaitUntilAsync(
            () => device.Status == ServiceStatus.Error && device.StatusMessage is not null,
            WaitTimeout,
            message: "An invalid definitions path should be reported as an error.");

        // Act
        device.ModelDefinitionsPath = null;
        await device.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => device.IsConnected && device.Units.GetValueOrDefault(1)?.Devices[0].Common?.Mn == "Vendor",
            WaitTimeout,
            message: "The device should connect once the path is fixed.");
        Assert.Equal(ServiceStatus.Running, device.Status);
    }

    [Fact]
    public async Task WhenTheSourceRestartsWithUnchangedDefinitions_ThenDynamicModelsAreKept()
    {
        // Arrange
        var directory = Path.Combine(Path.GetTempPath(), $"sunspec-models-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "model_64999.json"), SunSpecDynamicModelTests.DefinitionJson);
            var chain = new SunSpecTestChain()
                .AddModel(1)
                .AddModel(64999, new Dictionary<string, object?> { ["W"] = 1500, ["W_SF"] = 0, ["N"] = 0 }, definition: SunSpecDynamicModelTests.ParseDefinition());
            using var server = new SunSpecTestServer();
            server.Start((1, chain));
            await using var host = await HostedSunSpecDevice.StartAsync(server.Port, device => device.ModelDefinitionsPath = directory);
            var device = host.Device;

            SunSpecDynamicModel? GetDynamicModel() => device.Units.GetValueOrDefault(1)?.Devices[0].Models.OfType<SunSpecDynamicModel>().FirstOrDefault();
            await AsyncTestHelpers.WaitUntilAsync(
                () => GetDynamicModel()?.TryGetRegisteredSubject()?.TryGetProperty("W")?.GetValue() is 1500m,
                WaitTimeout,
                message: "The dynamic model should be read.");
            var model = GetDynamicModel();
            var discoveryCount = device.DiscoveryCount;

            // Act
            await device.ApplyConfigurationAsync(CancellationToken.None);

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(
                () => device.DiscoveryCount > discoveryCount && device.IsConnected,
                WaitTimeout,
                message: "The source should restart and discover again.");
            Assert.Same(model, GetDynamicModel());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
