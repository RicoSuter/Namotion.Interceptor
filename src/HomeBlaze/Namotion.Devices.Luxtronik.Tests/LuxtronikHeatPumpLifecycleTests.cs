using FluentModbus;
using HomeBlaze.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Devices.Luxtronik.Enums;
using Namotion.Devices.Luxtronik.Model;
using Namotion.Devices.Luxtronik.Tests.Testing;
using Namotion.Interceptor;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;

namespace Namotion.Devices.Luxtronik.Tests;

[Trait("Category", "Integration")]
[Collection(LuxtronikIntegrationCollection.Name)]
public class LuxtronikHeatPumpLifecycleTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task WhenControllerStopsAndRestarts_ThenHostedHeatPumpReportsErrorAndRecovers()
    {
        // Arrange
        using var server = new LuxtronikTestServer(new Version(3, 92, 3));
        server.Start();
        server.SeedTypicalValues();

        await using var host = await HostedHeatPump.StartAsync("127.0.0.1", server.Port);
        var heatPump = host.HeatPump;
        await AsyncTestHelpers.WaitUntilAsync(
            () => heatPump.IsConnected && heatPump.Status == ServiceStatus.Running && heatPump.LastUpdated is not null,
            WaitTimeout,
            message: "The hosted heat pump should connect and poll.");

        // Act & Assert
        server.Stop();
        await AsyncTestHelpers.WaitUntilAsync(
            () => !heatPump.IsConnected && heatPump.Status == ServiceStatus.Error,
            WaitTimeout,
            message: "The heat pump should report the lost controller.");

        server.Start();
        server.SeedTypicalValues();
        await AsyncTestHelpers.WaitUntilAsync(
            () => heatPump.IsConnected && heatPump.Status == ServiceStatus.Running,
            TimeSpan.FromSeconds(60),
            message: "The heat pump should recover once the controller is back.");
    }

    [Fact]
    public async Task WhenHostStops_ThenHeatPumpStopsWithoutWarnings()
    {
        // Arrange
        using var server = new LuxtronikTestServer(new Version(3, 92, 3));
        server.Start();
        server.SeedTypicalValues();

        var logger = new RecordingLogger<LuxtronikHeatPump>();
        var host = await HostedHeatPump.StartAsync("127.0.0.1", server.Port, logger);
        await AsyncTestHelpers.WaitUntilAsync(
            () => host.HeatPump.IsConnected && host.HeatPump.Status == ServiceStatus.Running,
            WaitTimeout,
            message: "The hosted heat pump should connect.");

        // Act
        await host.DisposeAsync();

        // Assert
        Assert.Empty(logger.Warnings);
        Assert.Equal(ServiceStatus.Stopped, host.HeatPump.Status);
    }

    [Fact]
    public async Task WhenPortChangesAndConfigurationIsApplied_ThenHeatPumpReadsTheNewController()
    {
        // Arrange
        using var firstServer = new LuxtronikTestServer(new Version(3, 92, 3));
        firstServer.Start();
        firstServer.SeedTypicalValues();

        using var secondServer = new LuxtronikTestServer(new Version(3, 92, 3));
        secondServer.Start();
        secondServer.SeedTypicalValues();
        secondServer.SetInput<short>(10108, 123);

        await using var host = await HostedHeatPump.StartAsync("127.0.0.1", firstServer.Port);
        var heatPump = host.HeatPump;
        await AsyncTestHelpers.WaitUntilAsync(
            () => heatPump.Temperatures.Outside.Temperature == -4.5m,
            WaitTimeout,
            message: "The heat pump should read the first controller.");

        // Act
        heatPump.Port = secondServer.Port;
        await heatPump.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => heatPump.Temperatures.Outside.Temperature == 12.3m && heatPump.Status == ServiceStatus.Running,
            WaitTimeout,
            message: "The heat pump should restart and read the second controller.");
    }

    [Fact]
    public async Task WhenNoHostAddressIsConfigured_ThenHeatPumpIsStoppedUntilOneIsApplied()
    {
        // Arrange
        using var server = new LuxtronikTestServer(new Version(3, 92, 3));
        server.Start();
        server.SeedTypicalValues();

        // Act & Assert
        await using var host = await HostedHeatPump.StartAsync(hostAddress: null, server.Port);
        var heatPump = host.HeatPump;
        await AsyncTestHelpers.WaitUntilAsync(
            () => heatPump.StatusMessage == "No host address configured",
            WaitTimeout,
            message: "The heat pump should report the missing host address.");
        Assert.Equal(ServiceStatus.Stopped, heatPump.Status);
        Assert.False(heatPump.IsConnected);

        heatPump.HostAddress = "127.0.0.1";
        await heatPump.ApplyConfigurationAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(
            () => heatPump.IsConnected && heatPump.Status == ServiceStatus.Running,
            WaitTimeout,
            message: "The heat pump should connect once a host address is applied.");
    }

    [Fact]
    public async Task WhenConfigurationIsInvalid_ThenHeatPumpReportsErrorUntilItIsFixed()
    {
        // Arrange
        using var server = new LuxtronikTestServer(new Version(3, 92, 3));
        server.Start();
        server.SeedTypicalValues();

        // Act & Assert
        await using var host = await HostedHeatPump.StartAsync("127.0.0.1", port: 0);
        var heatPump = host.HeatPump;
        await AsyncTestHelpers.WaitUntilAsync(
            () => heatPump.Status == ServiceStatus.Error,
            WaitTimeout,
            message: "The heat pump should report the invalid configuration.");
        Assert.Contains("Port", heatPump.StatusMessage);

        heatPump.Port = server.Port;
        await heatPump.ApplyConfigurationAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(
            () => heatPump.IsConnected && heatPump.Status == ServiceStatus.Running,
            WaitTimeout,
            message: "The heat pump should connect once the configuration is fixed.");
    }

    [Fact]
    public async Task WhenControllerReportsAnError_ThenHeatPumpStaysRunningAndReportsIt()
    {
        // Arrange
        using var server = new LuxtronikTestServer(new Version(3, 92, 3));
        server.Start();
        server.SeedTypicalValues();
        server.SetInput<ushort>(10201, 715);

        // Act
        await using var host = await HostedHeatPump.StartAsync("127.0.0.1", server.Port);
        var heatPump = host.HeatPump;

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => heatPump.StatusMessage == "Heat pump error 715",
            WaitTimeout,
            message: "The heat pump should report the controller error.");
        Assert.Equal(ServiceStatus.Running, heatPump.Status);
        Assert.True(heatPump.IsConnected);
    }

    [Fact]
    public async Task WhenFunctionsStayTheSame_ThenHeatPumpDoesNotDiscoverAgain()
    {
        // Arrange
        using var server = new LuxtronikTestServer(new Version(3, 92, 3));
        server.Start();
        server.SeedTypicalValues();

        await using var host = await HostedHeatPump.StartAsync("127.0.0.1", server.Port);
        var heatPump = host.HeatPump;
        await AsyncTestHelpers.WaitUntilAsync(
            () => heatPump.IsConnected && heatPump.Functions.GetFunctionMask() is not null && heatPump.LastUpdated is not null,
            WaitTimeout,
            message: "The heat pump should connect and read the flags.");

        // Act
        for (var poll = 0; poll < 2; poll++)
        {
            var lastUpdated = heatPump.LastUpdated;
            await AsyncTestHelpers.WaitUntilAsync(
                () => heatPump.LastUpdated > lastUpdated,
                WaitTimeout,
                message: "The heat pump should keep polling.");
        }

        // Assert
        Assert.Equal(1, heatPump.DiscoveryCount);
        Assert.True(heatPump.IsConnected);
    }

    [Fact]
    public async Task WhenCoolingIsSwitchedOnAndOffAgain_ThenTheCoolingSubjectAppearsAndIsRemoved()
    {
        // Arrange
        using var server = new LuxtronikTestServer(new Version(3, 92, 3));
        server.Start();
        server.SeedTypicalValues();
        server.SetInput<ushort>(10006, (ushort)LuxtronikModeStatus.Running);
        var allFunctions = Enum.GetValues<LuxtronikFunction>().Where(function => function != LuxtronikFunction.None).ToArray();
        var withoutCooling = allFunctions.Where(function => function != LuxtronikFunction.Cooling).ToArray();
        server.SetFunctions(withoutCooling);

        await using var host = await HostedHeatPump.StartAsync("127.0.0.1", server.Port);
        var heatPump = host.HeatPump;
        await AsyncTestHelpers.WaitUntilAsync(
            () => heatPump.Functions.IsCoolingEnabled == false && heatPump.Temperatures.Outside.Temperature == -4.5m,
            WaitTimeout,
            message: "The heat pump should read the flags without cooling.");
        Assert.Null(heatPump.Cooling);

        // Act
        server.SetFunctions(allFunctions);
        await AsyncTestHelpers.WaitUntilAsync(
            () => heatPump.Cooling?.Status == LuxtronikModeStatus.Running,
            WaitTimeout,
            message: "Cooling should appear and read its status.");
        var cooling = heatPump.Cooling!;
        Assert.Equal(2, heatPump.DiscoveryCount);
        server.SetFunctions(withoutCooling);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => heatPump.Cooling is null,
            WaitTimeout,
            message: "Cooling should be removed once its flag is clear.");
        Assert.False(new PropertyReference(cooling, nameof(LuxtronikCooling.Status)).TryGetSource(out _));
        Assert.Equal(3, heatPump.DiscoveryCount);
    }

    [Fact]
    public async Task WhenFunctionsChangeWhileStoppedAndTheirPollFails_ThenTheHeatPumpDiscoversOnlyOnce()
    {
        // Arrange
        using var server = new LuxtronikTestServer(new Version(3, 92, 3));
        server.Start();
        server.SeedTypicalValues();
        server.SetFunctions(LuxtronikFunction.Heating, LuxtronikFunction.HotWater);

        await using var host = await HostedHeatPump.StartAsync("127.0.0.1", server.Port);
        var heatPump = host.HeatPump;
        await AsyncTestHelpers.WaitUntilAsync(
            () => heatPump.IsConnected && heatPump.Functions.IsCoolingEnabled == false,
            WaitTimeout,
            message: "The heat pump should read the flags without cooling.");

        server.Stop();
        await AsyncTestHelpers.WaitUntilAsync(
            () => !heatPump.IsConnected,
            WaitTimeout,
            message: "The heat pump should report the lost controller.");
        var discoveryCount = heatPump.DiscoveryCount;

        // Act
        // The restarted server has every function active; only the discovery reads the flags, every poll of them fails.
        server.RejectFunctionReads(int.MaxValue, ModbusExceptionCode.ServerDeviceBusy, allowedReads: 1);
        server.Start();
        server.SeedTypicalValues();

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => heatPump.IsConnected && heatPump.Functions.IsCoolingEnabled == true && heatPump.Cooling is not null,
            TimeSpan.FromSeconds(60),
            message: "The discovered flags should be applied although their poll fails.");
        for (var statusUpdate = 0; statusUpdate < 2; statusUpdate++)
        {
            var lastUpdated = heatPump.LastUpdated;
            await AsyncTestHelpers.WaitUntilAsync(
                () => heatPump.LastUpdated > lastUpdated,
                WaitTimeout,
                message: "The heat pump should keep polling.");
        }

        Assert.Equal(discoveryCount + 1, heatPump.DiscoveryCount);
        Assert.True(heatPump.IsConnected);
    }

    [Fact]
    public async Task WhenMixingCircuitHeatingIsSwitchedOn_ThenItsMinimumAndMaximumAreRead()
    {
        // Arrange
        using var server = new LuxtronikTestServer(new Version(3, 92, 3));
        server.Start();
        server.SeedTypicalValues();
        server.SetInput<short>(10152, 200);
        server.SetInput<short>(10153, 450);
        server.SetFunctions(LuxtronikFunction.Heating, LuxtronikFunction.HotWater, LuxtronikFunction.MixingCircuit2Cooling);

        await using var host = await HostedHeatPump.StartAsync("127.0.0.1", server.Port);
        var heatPump = host.HeatPump;
        await AsyncTestHelpers.WaitUntilAsync(
            () => heatPump.MixingCircuit2 is not null && heatPump.Temperatures.Outside.Temperature == -4.5m,
            WaitTimeout,
            message: "Mixing circuit 2 should exist for cooling.");
        var circuit = heatPump.MixingCircuit2!;
        Assert.Null(circuit.MinimumTarget);

        // Act
        server.SetFunctions(LuxtronikFunction.Heating, LuxtronikFunction.HotWater, LuxtronikFunction.MixingCircuit2Cooling, LuxtronikFunction.MixingCircuit2Heating);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => heatPump.MixingCircuit2?.MinimumTarget == 20.0m && heatPump.MixingCircuit2?.MaximumTarget == 45.0m,
            WaitTimeout,
            message: "The minimum and maximum targets should be read once the circuit heats.");
        Assert.Same(circuit, heatPump.MixingCircuit2);
    }

    [Fact]
    public async Task WhenMixingCircuitHeatingIsSwitchedOff_ThenItsHeatingValuesAreCleared()
    {
        // Arrange
        using var server = new LuxtronikTestServer(new Version(3, 92, 3));
        server.Start();
        server.SeedTypicalValues();
        server.SetInput<short>(10151, 350);
        server.SetInput<short>(10152, 200);
        server.SetInput<short>(10153, 450);
        server.SetFunctions(LuxtronikFunction.Heating, LuxtronikFunction.HotWater, LuxtronikFunction.MixingCircuit2Cooling, LuxtronikFunction.MixingCircuit2Heating);

        await using var host = await HostedHeatPump.StartAsync("127.0.0.1", server.Port);
        var heatPump = host.HeatPump;
        await AsyncTestHelpers.WaitUntilAsync(
            () => heatPump.MixingCircuit2?.MinimumTarget == 20.0m && heatPump.MixingCircuit2?.MaximumTarget == 45.0m,
            WaitTimeout,
            message: "The minimum and maximum targets should be read while the circuit heats.");
        var circuit = heatPump.MixingCircuit2!;

        // Act
        server.SetFunctions(LuxtronikFunction.Heating, LuxtronikFunction.HotWater, LuxtronikFunction.MixingCircuit2Cooling);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => circuit.MinimumTarget is null && circuit.MaximumTarget is null,
            WaitTimeout,
            message: "The minimum and maximum targets should be cleared once the circuit only cools.");
        Assert.Same(circuit, heatPump.MixingCircuit2);
        Assert.Equal(35.0m, circuit.Target);
    }

    private sealed class HostedHeatPump : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly IHostedService _handler;

        private HostedHeatPump(ServiceProvider provider, IHostedService handler, LuxtronikHeatPump heatPump)
        {
            _provider = provider;
            _handler = handler;
            HeatPump = heatPump;
        }

        public LuxtronikHeatPump HeatPump { get; }

        public static async Task<HostedHeatPump> StartAsync(string? hostAddress, int port, ILogger<LuxtronikHeatPump>? logger = null)
        {
            var services = new ServiceCollection()
                .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            var context = InterceptorSubjectContext.Create()
                .WithFullPropertyTracking()
                .WithRegistry()
                .WithHostedServices(services);
            var provider = services.BuildServiceProvider();
            IHostedService handler;
            try
            {
                handler = Assert.Single(provider.GetServices<IHostedService>());
                await handler.StartAsync(CancellationToken.None);
            }
            catch
            {
                await provider.DisposeAsync();
                throw;
            }

            var heatPump = new LuxtronikHeatPump(logger ?? NullLogger<LuxtronikHeatPump>.Instance)
            {
                HostAddress = hostAddress,
                Port = port,
                PollingInterval = TimeSpan.FromSeconds(LuxtronikHeatPump.MinimumPollingIntervalSeconds)
            };

            var host = new HostedHeatPump(provider, handler, heatPump);
            _ = new TestHost(context) { HeatPump = heatPump };
            return host;
        }

        public async ValueTask DisposeAsync()
        {
            await _handler.StopAsync(CancellationToken.None);
            await _provider.DisposeAsync();
        }
    }
}
