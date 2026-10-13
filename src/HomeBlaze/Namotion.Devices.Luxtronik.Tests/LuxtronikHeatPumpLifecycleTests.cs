using System.Diagnostics;
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
using Namotion.Interceptor.Connectors.Monitoring;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.Modbus.Client;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;

namespace Namotion.Devices.Luxtronik.Tests;

[Trait("Category", "Integration")]
[Collection(LuxtronikIntegrationCollection.Name)]
public class LuxtronikHeatPumpLifecycleTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);

    // Below the product minimum, so each test observes several polls within its wait timeouts.
    private static readonly TimeSpan TestPollingInterval = TimeSpan.FromSeconds(2);

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

        // Act
        server.Stop();

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => !heatPump.IsConnected && heatPump.Status == ServiceStatus.Error,
            WaitTimeout,
            message: "The heat pump should report the lost controller.");

        // Act
        server.Start();
        server.SeedTypicalValues();

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => heatPump.IsConnected && heatPump.Status == ServiceStatus.Running,
            TimeSpan.FromSeconds(60),
            message: "The heat pump should recover once the controller is back.");
    }

    [Fact]
    public async Task WhenHostStops_ThenHeatPumpStopsWithoutWarningsOrErrors()
    {
        // Arrange
        using var server = new LuxtronikTestServer(new Version(3, 92, 3));
        server.Start();
        server.SeedTypicalValues();

        var logger = new RecordingLogger<LuxtronikHeatPump>();
        var host = await HostedHeatPump.StartAsync("127.0.0.1", server.Port, logger);
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(
                () => host.HeatPump.IsConnected && host.HeatPump.Status == ServiceStatus.Running,
                WaitTimeout,
                message: "The hosted heat pump should connect.");
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }

        // Act
        await host.DisposeAsync();

        // Assert
        Assert.Empty(logger.Warnings);
        Assert.Empty(logger.Errors);
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
    public async Task WhenConfigurationIsApplied_ThenTheOldSourceIsStoppedAndDisposedBeforeTheNewOneIsAttached()
    {
        // Arrange
        using var firstServer = new LuxtronikTestServer(new Version(3, 92, 3));
        firstServer.Start();
        firstServer.SeedTypicalValues();

        using var secondServer = new LuxtronikTestServer(new Version(3, 92, 3));
        secondServer.Start();
        secondServer.SeedTypicalValues();

        await using var host = await HostedHeatPump.StartAsync("127.0.0.1", firstServer.Port);
        var heatPump = host.HeatPump;
        await AsyncTestHelpers.WaitUntilAsync(
            () => heatPump.IsConnected && heatPump.Status == ServiceStatus.Running,
            WaitTimeout,
            message: "The heat pump should connect to the first controller.");

        var oldAttachment = Assert.Single(heatPump.GetHostedServiceAttachments());
        var oldSource = Assert.IsType<ModbusSubjectClientSource>(oldAttachment.Current);

        // Act
        heatPump.Port = secondServer.Port;
        await heatPump.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        // Polled tightly: the old stop is a few tens of milliseconds, and a restart that does not wait for it
        // puts the new attachment on the subject while the old one is still Stopping.
        await AsyncTestHelpers.WaitUntilAsync(
            () => heatPump.GetHostedServiceAttachments() is [var attachment] && !ReferenceEquals(attachment, oldAttachment),
            WaitTimeout,
            pollInterval: TimeSpan.FromMilliseconds(1),
            message: "A new attachment should replace the old one.");

        // Removed rather than Stopping: the handler disposes an instance it created before it leaves the stop window.
        Assert.Equal(HostedServiceAttachmentState.Removed, oldAttachment.GetState(out var oldCurrent));
        Assert.Null(oldCurrent);
        Assert.Equal(SourceState.Stopped, oldSource.State);

        await AsyncTestHelpers.WaitUntilAsync(
            () => heatPump.IsConnected && heatPump.Status == ServiceStatus.Running,
            WaitTimeout,
            message: "The heat pump should connect to the second controller.");
        var newAttachment = Assert.Single(heatPump.GetHostedServiceAttachments());
        Assert.NotSame(oldSource, newAttachment.Current);
    }

    [Fact]
    public async Task WhenTheHandlerStopsWhileASourceIsAttached_ThenItStopsWellInsideTheShutdownTimeout()
    {
        // Arrange
        using var server = new LuxtronikTestServer(new Version(3, 92, 3));
        server.Start();
        server.SeedTypicalValues();

        await using var host = await HostedHeatPump.StartAsync("127.0.0.1", server.Port);
        var heatPump = host.HeatPump;
        await AsyncTestHelpers.WaitUntilAsync(
            () => heatPump.IsConnected && heatPump.Status == ServiceStatus.Running,
            WaitTimeout,
            message: "The heat pump should connect, or the stop below proves nothing.");
        var attachment = Assert.Single(heatPump.GetHostedServiceAttachments());

        // Act
        // A subject that detaches its own attachment from its unwind waits on a stop ordered behind its own, and the
        // handler only gives up on that at the shutdown deadline, so the elapsed time tells the two apart.
        var shutdownTimeout = TimeSpan.FromSeconds(20);
        using var shutdown = new CancellationTokenSource(shutdownTimeout);
        var stopwatch = Stopwatch.StartNew();
        await host.StopHandlerAsync(shutdown.Token);
        stopwatch.Stop();

        // Assert
        Assert.True(
            stopwatch.Elapsed < shutdownTimeout / 2,
            $"Shutdown took {stopwatch.Elapsed.TotalSeconds:F1} seconds of a {shutdownTimeout.TotalSeconds:F0} second timeout.");
        Assert.Equal(HostedServiceAttachmentState.Stopped, attachment.GetState(out var current));
        Assert.Null(current);
        Assert.Equal(ServiceStatus.Stopped, heatPump.Status);
        Assert.False(heatPump.IsConnected);
    }

    [Fact]
    public async Task WhenHeatPumpLeavesAndReEntersTheGraph_ThenTheSameAttachmentRunsANewSource()
    {
        // Arrange
        using var server = new LuxtronikTestServer(new Version(3, 92, 3));
        server.Start();
        server.SeedTypicalValues();

        await using var host = await HostedHeatPump.StartAsync("127.0.0.1", server.Port);
        var heatPump = host.HeatPump;
        await AsyncTestHelpers.WaitUntilAsync(
            () => heatPump.IsConnected && heatPump.Status == ServiceStatus.Running,
            WaitTimeout,
            message: "The heat pump should connect.");
        var attachment = Assert.Single(heatPump.GetHostedServiceAttachments());
        var firstSource = attachment.Current;

        // Act
        host.Container.HeatPump = null;
        await AsyncTestHelpers.WaitUntilAsync(
            () => heatPump.Status == ServiceStatus.Stopped && attachment.GetState(out _) == HostedServiceAttachmentState.Stopped,
            WaitTimeout,
            message: "Leaving the graph should stop the heat pump and its source.");
        host.Container.HeatPump = heatPump;

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => heatPump.IsConnected && heatPump.Status == ServiceStatus.Running && attachment.Current is not null,
            WaitTimeout,
            message: "Re-entering the graph should run a source again.");
        Assert.Same(attachment, Assert.Single(heatPump.GetHostedServiceAttachments()));
        Assert.NotSame(firstSource, attachment.Current);
    }

    [Fact]
    public async Task WhenHeatPumpEntersTheGraph_ThenTheSourceHoldIsTakenBeforeTheHeatPumpHoldIsReleased()
    {
        // Arrange
        using var server = new LuxtronikTestServer(new Version(3, 92, 3));
        server.Start();
        server.SeedTypicalValues();
        var recorder = new StartupCompletionRecorder();

        // Act
        await using var host = await HostedHeatPump.StartAsync(
            "127.0.0.1", server.Port, configureContext: context => context.AddService<IStartupCompletion>(recorder));
        var heatPump = host.HeatPump;
        await AsyncTestHelpers.WaitUntilAsync(
            () => heatPump.IsConnected && heatPump.Status == ServiceStatus.Running && recorder.Outstanding == 0,
            WaitTimeout,
            message: "The heat pump should connect and every completion deferral should have been released.");

        // Assert
        // The heat pump's own completion deferral and its source's. Settling in between is a startup
        // completion wait passing while the source it waits for has not been attached yet.
        Assert.Equal(2, recorder.DeferralCount);
        Assert.Equal(2, recorder.DeferralCountWhenFirstSettled);
    }

    [Fact]
    public async Task WhenNoHostAddressIsConfigured_ThenHeatPumpIsStoppedUntilOneIsApplied()
    {
        // Arrange
        using var server = new LuxtronikTestServer(new Version(3, 92, 3));
        server.Start();
        server.SeedTypicalValues();

        // Act
        await using var host = await HostedHeatPump.StartAsync(hostAddress: null, server.Port);
        var heatPump = host.HeatPump;

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => heatPump.StatusMessage == "No host address configured",
            WaitTimeout,
            message: "The heat pump should report the missing host address.");
        Assert.Equal(ServiceStatus.Stopped, heatPump.Status);
        Assert.False(heatPump.IsConnected);

        // Act
        heatPump.HostAddress = "127.0.0.1";
        await heatPump.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
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

        var logger = new RecordingLogger<LuxtronikHeatPump>();

        // Act
        await using var host = await HostedHeatPump.StartAsync("127.0.0.1", port: 0, logger);
        var heatPump = host.HeatPump;

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => heatPump.Status == ServiceStatus.Error,
            WaitTimeout,
            message: "The heat pump should report the invalid configuration.");
        Assert.Contains("Port", heatPump.StatusMessage);

        // Rejected before any attach, rather than reported once an attached source has faulted on it.
        var error = Assert.Single(logger.Errors);
        Assert.Contains("invalid configuration", error);

        // Act
        heatPump.Port = server.Port;
        await heatPump.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
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
        await WaitForPollsAsync(heatPump, 2);

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
        var discoveryCount = heatPump.DiscoveryCount;
        server.SetFunctions(allFunctions);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => heatPump.Cooling?.Status == LuxtronikModeStatus.Running,
            WaitTimeout,
            message: "Cooling should appear and read its status.");
        Assert.Equal(discoveryCount + 1, heatPump.DiscoveryCount);

        // Act
        discoveryCount = heatPump.DiscoveryCount;
        server.SetFunctions(withoutCooling);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => heatPump.Cooling is null,
            WaitTimeout,
            message: "Cooling should be removed once its flag is clear.");
        Assert.Equal(discoveryCount + 1, heatPump.DiscoveryCount);
    }

    [Fact]
    public async Task WhenHotWaterIsSwitchedOff_ThenHeatPumpDoesNotDiscoverAgain()
    {
        // Arrange
        using var server = new LuxtronikTestServer(new Version(3, 92, 3));
        server.Start();
        server.SeedTypicalValues();

        await using var host = await HostedHeatPump.StartAsync("127.0.0.1", server.Port);
        var heatPump = host.HeatPump;
        await AsyncTestHelpers.WaitUntilAsync(
            () => heatPump.IsConnected && heatPump.Functions.IsHotWaterEnabled == true,
            WaitTimeout,
            message: "The heat pump should connect and read the flags.");

        // Act
        server.SetFunctions(Enum.GetValues<LuxtronikFunction>()
            .Where(function => function is not LuxtronikFunction.None and not LuxtronikFunction.HotWater)
            .ToArray());
        await AsyncTestHelpers.WaitUntilAsync(
            () => heatPump.Functions.IsHotWaterEnabled == false,
            WaitTimeout,
            message: "The heat pump should read the cleared hot water flag.");
        await WaitForPollsAsync(heatPump, 2);

        // Assert
        Assert.Equal(1, heatPump.DiscoveryCount);
        Assert.True(heatPump.IsConnected);
    }

    [Fact]
    public async Task WhenControllerDoesNotReportItsFunctions_ThenHeatPumpDoesNotDiscoverAgain()
    {
        // Arrange
        using var server = new LuxtronikTestServer(new Version(3, 92, 3), supportsDiscreteInputs: false);
        server.Start();
        server.SeedTypicalValues();

        await using var host = await HostedHeatPump.StartAsync("127.0.0.1", server.Port);
        var heatPump = host.HeatPump;
        await AsyncTestHelpers.WaitUntilAsync(
            () => heatPump.IsConnected && heatPump.LastUpdated is not null,
            WaitTimeout,
            message: "The heat pump should connect.");

        // Act
        await WaitForPollsAsync(heatPump, 2);

        // Assert
        Assert.Equal(1, heatPump.DiscoveryCount);
        Assert.Null(heatPump.Functions.GetFunctionMask());
        Assert.NotNull(heatPump.Cooling);
    }

    private static async Task WaitForPollsAsync(LuxtronikHeatPump heatPump, int count)
    {
        for (var poll = 0; poll < count; poll++)
        {
            var lastUpdated = heatPump.LastUpdated;
            await AsyncTestHelpers.WaitUntilAsync(
                () => heatPump.LastUpdated > lastUpdated,
                WaitTimeout,
                message: "The heat pump should keep polling.");
        }
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

        private HostedHeatPump(ServiceProvider provider, IHostedService handler, LuxtronikHeatPump heatPump, TestHost container)
        {
            _provider = provider;
            _handler = handler;
            HeatPump = heatPump;
            Container = container;
        }

        public LuxtronikHeatPump HeatPump { get; }

        /// <summary>The graph root the heat pump sits in; clearing its property takes the heat pump out of the graph.</summary>
        public TestHost Container { get; }

        public static async Task<HostedHeatPump> StartAsync(
            string? hostAddress,
            int port,
            ILogger<LuxtronikHeatPump>? logger = null,
            Action<IInterceptorSubjectContext>? configureContext = null)
        {
            var services = new ServiceCollection()
                .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            var context = InterceptorSubjectContext.Create()
                .WithFullPropertyTracking()
                .WithRegistry()
                .WithHostedServices(services);
            configureContext?.Invoke(context);
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
                MinimumPollingInterval = TestPollingInterval,
                PollingInterval = TestPollingInterval
            };

            var container = new TestHost(context);
            var host = new HostedHeatPump(provider, handler, heatPump, container);
            container.HeatPump = heatPump;
            return host;
        }

        /// <summary>Stops the hosting handler the way a host shutdown does, bounded by <paramref name="shutdownToken"/>.</summary>
        public Task StopHandlerAsync(CancellationToken shutdownToken) => _handler.StopAsync(shutdownToken);

        public async ValueTask DisposeAsync()
        {
            await _handler.StopAsync(CancellationToken.None);
            await _provider.DisposeAsync();
        }
    }
}
