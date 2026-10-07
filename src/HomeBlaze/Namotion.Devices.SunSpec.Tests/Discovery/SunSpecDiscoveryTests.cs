using HomeBlaze.Abstractions;
using Namotion.Devices.SunSpec.Models;
using Namotion.Devices.SunSpec.Tests.Models;
using Namotion.Devices.SunSpec.Tests.Testing;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;

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
        Assert.Null(device.StatusMessage);
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
