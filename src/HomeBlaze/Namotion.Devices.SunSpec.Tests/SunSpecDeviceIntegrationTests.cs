using System.Collections.Concurrent;
using System.Reactive.Linq;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Devices.Energy;
using Namotion.Devices.SunSpec.Models;
using Namotion.Devices.SunSpec.Tests.Testing;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;

namespace Namotion.Devices.SunSpec.Tests;

[Trait("Category", "Integration")]
[Collection(SunSpecIntegrationCollection.Name)]
public class SunSpecDeviceIntegrationTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);

    private static SunSpecTestChain CreateSolarEdgeChain() => new SunSpecTestChain()
        .AddModel(1, new Dictionary<string, object?> { ["Mn"] = "SolarEdge", ["Md"] = "SE-NX20K", ["Vr"] = "4.20.1", ["SN"] = "7E1A2B3C", ["DA"] = 1 }, length: 65)
        .AddModel(103, new Dictionary<string, object?>
        {
            ["W"] = 1500, ["W_SF"] = 0, ["WH"] = 123456, ["WH_SF"] = 0, ["Hz"] = 5000, ["Hz_SF"] = -2, ["TmpCab"] = 4520, ["Tmp_SF"] = -2
        })
        .AddModel(1, new Dictionary<string, object?> { ["Mn"] = "SolarEdge", ["Md"] = "Backup Interface", ["SN"] = "M1" }, length: 65)
        .AddModel(203, new Dictionary<string, object?> { ["W"] = -800, ["W_SF"] = 0, ["TotWhImp"] = 1000, ["TotWhExp"] = 2000, ["TotWh_SF"] = 0, ["Hz"] = 5001, ["Hz_SF"] = -2 })
        .AddModel(1, new Dictionary<string, object?> { ["Mn"] = "SolarEdge", ["Md"] = "Meter", ["SN"] = "M2" }, length: 65)
        .AddModel(203, new Dictionary<string, object?> { ["W"] = 300, ["W_SF"] = 0 });

    private static SunSpecInverter? GetInverter(SunSpecDevice device)
        => device.Units.GetValueOrDefault(1)?.Devices.FirstOrDefault()?.Models.OfType<SunSpecInverter>().FirstOrDefault();

    [Fact]
    public async Task WhenSolarEdgeLayoutIsServed_ThenInverterAndMetersAreDiscoveredAndRead()
    {
        // Arrange
        using var server = new SunSpecTestServer();
        server.Start((1, CreateSolarEdgeChain()));

        // Act
        await using var host = await HostedSunSpecDevice.StartAsync(server.Port);
        var device = host.Device;

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => GetInverter(device)?.MeasuredPower == -1500m, WaitTimeout, message: "The inverter should be read.");
        var devices = device.Units[1].Devices;
        Assert.Equal(3, devices.Length);
        Assert.Equal("SolarEdge SE-NX20K", devices[0].Title);
        Assert.Equal("4.20.1", devices[0].SoftwareVersion);
        var inverter = GetInverter(device)!;
        Assert.Equal(40069, inverter.BaseAddress);
        Assert.Equal(123456m, inverter.TotalExportedEnergy);
        Assert.Equal(50m, inverter.ElectricalFrequency);
        Assert.Equal(45.2m, inverter.Temperature);
        var meter = Assert.IsType<SunSpecAcMeter>(Assert.Single(devices[1].Models));
        await AsyncTestHelpers.WaitUntilAsync(() => meter.MeasuredPower == -800m, WaitTimeout, message: "The meter should be read.");
        Assert.Equal(1000m, meter.TotalImportedEnergy);
        Assert.Equal(40295, devices[2].Common!.BaseAddress);
        await AsyncTestHelpers.WaitUntilAsync(() => device.Status == ServiceStatus.Running, WaitTimeout, message: "The device should be running.");
    }

    [Fact]
    public async Task WhenSeveralUnitIdsAreConfigured_ThenEachIsDiscoveredOverOneConnection()
    {
        // Arrange
        using var server = new SunSpecTestServer(1, 2);
        var batteryChain = new SunSpecTestChain()
            .AddModel(1, new Dictionary<string, object?> { ["Mn"] = "Battery" })
            .AddModel(713, new Dictionary<string, object?> { ["SoC"] = 455, ["Pct_SF"] = -1 });
        server.Start((1, CreateSolarEdgeChain()), (2, batteryChain));

        // Act
        await using var host = await HostedSunSpecDevice.StartAsync(server.Port, device => device.UnitIds = [1, 2]);
        var device = host.Device;

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => device.Units.GetValueOrDefault(2)?.Devices[0].Models.OfType<IBatteryState>().FirstOrDefault()?.BatteryLevel == 0.455m,
            WaitTimeout,
            message: "The battery on unit 2 should be read.");
        Assert.NotNull(GetInverter(device));
        Assert.Equal(1, server.ConnectionCount);
    }

    [Fact]
    public async Task WhenValueAndScaleFactorChangeTogether_ThenNoTornValueIsPublished()
    {
        // Arrange
        using var server = new SunSpecTestServer();
        server.Start((1, CreateSolarEdgeChain()));
        await using var host = await HostedSunSpecDevice.StartAsync(server.Port);
        await AsyncTestHelpers.WaitUntilAsync(() => GetInverter(host.Device)?.W == 1500m, WaitTimeout, message: "The inverter should be read.");
        var inverter = GetInverter(host.Device)!;

        var observedPowers = new ConcurrentQueue<decimal?>();
        using var subscription = host.Context.GetPropertyChangeObservable()
            .Where(change => ReferenceEquals(change.Property.Subject, inverter) && change.Property.Name == nameof(SunSpecInverter.W))
            .Subscribe(change => observedPowers.Enqueue(change.GetNewValue<decimal?>()));

        // Act: W at offset 14 and W_SF at offset 15 change in one write, from 1500 * 10^0 to 16000 * 10^-1.
        server.WriteRegisters(40069 + 14, [16000, unchecked((ushort)-1)]);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => inverter.W == 1600m, WaitTimeout, message: "The new power should be read.");
        Assert.Equal(new decimal?[] { 1600m }, observedPowers);
    }

    [Fact]
    public async Task WhenPySunSpecDerDumpIsServed_ThenSixtyFourBitEnergyIsRead()
    {
        // Arrange
        using var server = new SunSpecTestServer();
        server.Start((1, SunSpecFixtures.LoadPySunSpec("device_1547.json", [1, 701], "VL3N")));

        // Act
        await using var host = await HostedSunSpecDevice.StartAsync(server.Port);

        // Assert
        SunSpecDerAcMeasurement? GetMeasurement() => host.Device.Units.GetValueOrDefault(1)?.Devices[0].Models.OfType<SunSpecDerAcMeasurement>().FirstOrDefault();
        await AsyncTestHelpers.WaitUntilAsync(() => GetMeasurement()?.TotWhInj == 150000m, WaitTimeout, message: "The 64-bit energy should be read.");
        var measurement = GetMeasurement()!;
        Assert.Equal(9800m, measurement.W);
        Assert.Equal(60.01m, measurement.Hz);
        Assert.Equal(55m, measurement.TmpCab);
    }

    [Fact]
    public async Task WhenPySunSpecCurveDumpIsServed_ThenRepeatingGroupsArePercentages()
    {
        // Arrange
        using var server = new SunSpecTestServer();
        server.Start((1, SunSpecFixtures.LoadPySunSpec("inverter_123.json", [1, 129])));

        // Act
        await using var host = await HostedSunSpecDevice.StartAsync(server.Port);

        // Assert
        SunSpecModel129? GetCurves() => host.Device.Units.GetValueOrDefault(1)?.Devices[0].Models.OfType<SunSpecModel129>().FirstOrDefault();
        await AsyncTestHelpers.WaitUntilAsync(() => GetCurves()?.Curve.FirstOrDefault()?.V1 == 0.88m, WaitTimeout, message: "The curve points should be read.");
        var curves = GetCurves()!;
        Assert.Equal(4, curves.Curve.Length);
        Assert.Equal(TimeSpan.FromSeconds(2), curves.Curve[0].Tms1);
    }

    [Fact]
    public async Task WhenModuleScaleFactorsLiveOnTheModel_ThenModulesAreScaledThroughTheProvider()
    {
        // Arrange
        var modules = new[]
        {
            new Dictionary<string, object?> { ["DCA"] = 1234 },
            new Dictionary<string, object?> { ["DCA"] = 567 }
        };
        var chain = new SunSpecTestChain()
            .AddModel(1, length: 65)
            .AddModel(103)
            .AddModel(160, new Dictionary<string, object?> { ["DCA_SF"] = -2, ["N"] = 2, ["module"] = modules });
        using var server = new SunSpecTestServer();
        server.Start((1, chain));

        // Act
        await using var host = await HostedSunSpecDevice.StartAsync(server.Port);

        // Assert
        SunSpecMppt? GetMppt() => host.Device.Units.GetValueOrDefault(1)?.Devices[0].Models.OfType<SunSpecMppt>().FirstOrDefault();
        await AsyncTestHelpers.WaitUntilAsync(() => GetMppt()?.Module.LastOrDefault()?.DCA == 5.67m, WaitTimeout, message: "The modules should be scaled.");
        Assert.Equal(12.34m, GetMppt()!.Module[0].DCA);
    }

    [Fact]
    public async Task WhenChainChangesWhileConnected_ThenTheNewChainIsDiscovered()
    {
        // Arrange
        using var server = new SunSpecTestServer();
        server.Start((1, new SunSpecTestChain().AddModel(1, length: 65).AddModel(103, new Dictionary<string, object?> { ["W"] = 1500, ["W_SF"] = 0 })));
        await using var host = await HostedSunSpecDevice.StartAsync(server.Port);
        await AsyncTestHelpers.WaitUntilAsync(() => GetInverter(host.Device)?.W == 1500m, WaitTimeout, message: "The inverter should be read.");

        // Act
        server.WriteChain(new SunSpecTestChain().AddModel(1, length: 65).AddModel(203, new Dictionary<string, object?> { ["W"] = 300, ["W_SF"] = 0 }));

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => host.Device.Units.GetValueOrDefault(1)?.Devices[0].Models.OfType<SunSpecAcMeter>().FirstOrDefault()?.MeasuredPower == 300m,
            WaitTimeout,
            message: "The changed chain should be discovered and read.");
        Assert.Null(GetInverter(host.Device));
    }

    [Fact]
    public async Task WhenDeviceReconnects_ThenUnchangedModelsAreKept()
    {
        // Arrange
        using var server = new SunSpecTestServer();
        server.Start((1, CreateSolarEdgeChain()));
        await using var host = await HostedSunSpecDevice.StartAsync(server.Port);
        await AsyncTestHelpers.WaitUntilAsync(() => host.Device.IsConnected && GetInverter(host.Device) is not null, WaitTimeout, message: "The inverter should be discovered.");
        var inverter = GetInverter(host.Device);

        // Act
        server.Stop();
        await AsyncTestHelpers.WaitUntilAsync(() => !host.Device.IsConnected, WaitTimeout, message: "The device should notice the lost connection.");
        server.Start((1, CreateSolarEdgeChain()));

        // Assert: the connector retries every RetryTime (10 s by default).
        await AsyncTestHelpers.WaitUntilAsync(() => host.Device.IsConnected, TimeSpan.FromSeconds(60), message: "The device should reconnect.");
        Assert.Same(inverter, GetInverter(host.Device));
    }

    [Fact]
    public async Task WhenNoUnitHasAMarker_ThenStatusSaysSo()
    {
        // Arrange
        using var server = new SunSpecTestServer();
        server.Start();

        // Act
        await using var host = await HostedSunSpecDevice.StartAsync(server.Port);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => host.Device.IsConnected && host.Device.StatusMessage == "No SunSpec unit found", WaitTimeout, message: "The missing marker should be reported.");
        Assert.Empty(host.Device.Units);
        Assert.Equal(ServiceStatus.Running, host.Device.Status);
    }

    [Fact]
    public async Task WhenConnectionIsRefused_ThenStatusHintsAtTheSingleClientLimit()
    {
        // Arrange: the server is never started, so nothing listens on its port.
        using var server = new SunSpecTestServer();

        // Act
        await using var host = await HostedSunSpecDevice.StartAsync(server.Port);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => host.Device.Status == ServiceStatus.Error && host.Device.StatusMessage == "Connection refused: the device may accept only one Modbus TCP client",
            WaitTimeout,
            message: "The refused connection should be reported.");
    }

    [Fact]
    public async Task WhenRegisterDumpIsEnabled_ThenTheChainIsLoggedAsAFixture()
    {
        // Arrange
        using var server = new SunSpecTestServer();
        server.Start((1, CreateSolarEdgeChain()));
        var logger = new RecordingLogger<SunSpecDevice>();

        // Act
        await using var host = await HostedSunSpecDevice.StartAsync(server.Port, device => device.IsRegisterDumpEnabled = true, logger);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => logger.Entries.Any(entry => entry.Message.Contains("\"markerAddress\":40000", StringComparison.Ordinal)),
            WaitTimeout,
            message: "The register dump should be logged.");
        var dump = logger.Entries.First(entry => entry.Message.Contains("\"markerAddress\"", StringComparison.Ordinal)).Message;
        var json = dump[dump.IndexOf('{', StringComparison.Ordinal)..];
        Assert.Equal(CreateSolarEdgeChain().Build(), SunSpecTestChain.FromDump(json).Build());
    }
}
