using FluentModbus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Monitoring;
using Namotion.Interceptor.Modbus.Attributes;
using Namotion.Interceptor.Modbus.Tests.Testing;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Modbus.Tests;

[Trait("Category", "Integration")]
[Collection(ModbusIntegrationCollection.Name)]
public partial class ModbusSubjectClientSourceTests
{
    private static readonly ModbusFunctionCode[] ReadFunctionCodes =
    [
        ModbusFunctionCode.ReadCoils,
        ModbusFunctionCode.ReadDiscreteInputs,
        ModbusFunctionCode.ReadHoldingRegisters,
        ModbusFunctionCode.ReadInputRegisters
    ];

    [InterceptorSubject]
    public partial class TestDevice : IModbusDiscovery
    {
        private int _discoveryCount;

        [ModbusRegister(0, ModbusDataType.S16, Scale = 0.1)]
        public partial decimal? Temperature { get; set; }

        [ModbusRegister(1, ModbusDataType.U16)]
        public partial int? Counter { get; set; }

        [ModbusRegister(10, ModbusDataType.U32, Space = ModbusAddressSpace.InputRegister)]
        public partial long? Energy { get; set; }

        [ModbusRegister(3, ModbusDataType.Boolean, Space = ModbusAddressSpace.Coil)]
        public partial bool? Pump { get; set; }

        [ModbusRegister(1, ModbusDataType.Boolean, Space = ModbusAddressSpace.DiscreteInput)]
        public partial bool? Alarm { get; set; }

        [ModbusRegister(20, ModbusDataType.U16)]
        public partial int? Optional { get; set; }

        public partial SecondUnit? Second { get; set; }

        public Func<ModbusDiscoveryContext, CancellationToken, Task>? OnDiscover { get; set; }

        public int DiscoveryCount => Volatile.Read(ref _discoveryCount);

        public Task DiscoverAsync(ModbusDiscoveryContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _discoveryCount);
            return OnDiscover?.Invoke(context, cancellationToken) ?? Task.CompletedTask;
        }
    }

    [ModbusUnitId(2)]
    [InterceptorSubject]
    public partial class SecondUnit
    {
        [ModbusRegister(0, ModbusDataType.U16)]
        public partial int? Value { get; set; }
    }

    private static void SeedServer(ModbusTestServer server)
    {
        server.SetHoldingRegister<short>(0, 215);
        server.SetHoldingRegister<ushort>(1, 42);
        server.SetHoldingRegister<ushort>(20, 7);
        server.SetInputRegister<uint>(10, 100000);
        server.SetCoil(3, true);
        server.SetDiscreteInput(1, true);
    }

    private static TestDevice CreateDevice(Action<TestDevice>? configure = null)
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry().WithLifecycle();
        var device = new TestDevice(context);
        configure?.Invoke(device);
        return device;
    }

    private static ModbusSubjectClientSource CreateSource(TestDevice device, ModbusTestServer server)
        => device.CreateModbusClientSource(
            new ModbusClientConfiguration
            {
                Host = "127.0.0.1",
                Port = server.Port,
                PollingInterval = TimeSpan.FromMilliseconds(100),
                RetryTime = TimeSpan.FromMilliseconds(200),
                RequestTimeout = TimeSpan.FromSeconds(2)
            },
            NullLogger.Instance);

    private static async Task<(TestDevice Device, ModbusSubjectClientSource Source, SourceStateRecorder Recorder)> StartAsync(
        ModbusTestServer server, Action<TestDevice>? configure = null)
    {
        var device = CreateDevice(configure);
        var source = CreateSource(device, server);

        var recorder = SourceStateRecorder.SubscribeTo(source);
        await source.StartAsync(CancellationToken.None);
        await recorder.WaitForStatesAsync(TimeSpan.FromSeconds(30), "The source should synchronize.", SourceState.Synchronized);
        return (device, source, recorder);
    }

    [Fact]
    public async Task WhenSourceStarts_ThenInitialStateIsLoaded()
    {
        // Arrange
        using var server = new ModbusTestServer();
        server.Start();
        SeedServer(server);

        // Act
        var (device, source, recorder) = await StartAsync(server);
        try
        {
            // Assert
            Assert.Equal(21.5m, device.Temperature);
            Assert.Equal(42, device.Counter);
            Assert.Equal(100000L, device.Energy);
            Assert.True(device.Pump);
            Assert.True(device.Alarm);
            Assert.Equal(7, device.Optional);
            Assert.Equal(1, device.DiscoveryCount);
        }
        finally
        {
            recorder.Dispose();
            await source.DisposeAsync();
        }
    }

    [Fact]
    public async Task WhenRegisterChanges_ThenPropertyIsUpdated()
    {
        // Arrange
        using var server = new ModbusTestServer();
        server.Start();
        SeedServer(server);
        var (device, source, recorder) = await StartAsync(server);
        try
        {
            // Act
            server.SetHoldingRegister<ushort>(1, 43);

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(() => device.Counter == 43, TimeSpan.FromSeconds(10), message: "Counter should update.");
        }
        finally
        {
            recorder.Dispose();
            await source.DisposeAsync();
        }
    }

    [Fact]
    public async Task WhenPropertyIsWrittenLocally_ThenNothingIsSentAndTheDeviceValueIsRestored()
    {
        // Arrange
        using var server = new ModbusTestServer();
        server.Start();
        SeedServer(server);
        var (device, source, recorder) = await StartAsync(server);
        try
        {
            // Act
            device.Counter = 999;

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(() => device.Counter == 42, TimeSpan.FromSeconds(10), message: "The device value should be restored.");
            Assert.All(server.Requests, request => Assert.Contains(request.FunctionCode, ReadFunctionCodes));
        }
        finally
        {
            recorder.Dispose();
            await source.DisposeAsync();
        }
    }

    [Fact]
    public async Task WhenDiscoveryExcludesProperty_ThenItIsNeitherClaimedNorRead()
    {
        // Arrange
        using var server = new ModbusTestServer();
        server.Start();
        SeedServer(server);

        // Act
        var (device, source, recorder) = await StartAsync(server, testDevice => testDevice.OnDiscover = (context, _) =>
        {
            context.ExcludeProperty(new PropertyReference(testDevice, nameof(TestDevice.Optional)));
            return Task.CompletedTask;
        });
        try
        {
            // Assert
            Assert.Null(device.Optional);
            Assert.False(new PropertyReference(device, nameof(TestDevice.Optional)).TryGetSource(out _));
            Assert.DoesNotContain(server.Requests, request =>
                request.FunctionCode == ModbusFunctionCode.ReadHoldingRegisters &&
                request.Address <= 20 && request.Address + request.Quantity > 20);
        }
        finally
        {
            recorder.Dispose();
            await source.DisposeAsync();
        }
    }

    [Fact]
    public async Task WhenDiscoveryReadsAllSpaces_ThenValuesAreReturnedAndTheContextExpiresAfterwards()
    {
        // Arrange
        using var server = new ModbusTestServer();
        server.Start();
        SeedServer(server);
        ushort[]? holdingRegisters = null;
        ushort[]? inputRegisters = null;
        bool[]? coils = null;
        bool[]? discreteInputs = null;
        ModbusDiscoveryContext? capturedContext = null;

        // Act
        var (_, source, recorder) = await StartAsync(server, testDevice => testDevice.OnDiscover = async (context, cancellationToken) =>
        {
            capturedContext = context;
            holdingRegisters = await context.ReadHoldingRegistersAsync(0, 2, cancellationToken: cancellationToken);
            inputRegisters = await context.ReadInputRegistersAsync(10, 2, cancellationToken: cancellationToken);
            coils = await context.ReadCoilsAsync(0, 4, cancellationToken: cancellationToken);
            discreteInputs = await context.ReadDiscreteInputsAsync(0, 2, cancellationToken: cancellationToken);
        });
        try
        {
            // Assert (100000 is 0x000186A0: high word 1, low word 0x86A0)
            Assert.Equal(new ushort[] { 215, 42 }, holdingRegisters);
            Assert.Equal(new ushort[] { 1, 0x86A0 }, inputRegisters);
            Assert.Equal(new[] { false, false, false, true }, coils);
            Assert.Equal(new[] { false, true }, discreteInputs);
            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                capturedContext!.ReadHoldingRegistersAsync(0, 1));
        }
        finally
        {
            recorder.Dispose();
            await source.DisposeAsync();
        }
    }

    [Fact]
    public async Task WhenDeviceRejectsARegister_ThenNeighboursUpdateAndTheRejectedOneIsUnavailable()
    {
        // Arrange
        using var server = new ModbusTestServer();
        server.Start();
        SeedServer(server);
        server.RejectAddress(ModbusAddressSpace.HoldingRegister, 1);

        // Act
        var (device, source, recorder) = await StartAsync(server);
        try
        {
            // Assert
            Assert.Equal(21.5m, device.Temperature);
            Assert.Null(device.Counter);
            Assert.Equal(1, source.Diagnostics.Polling.UnavailableProperties);
        }
        finally
        {
            recorder.Dispose();
            await source.DisposeAsync();
        }
    }

    [Fact]
    public async Task WhenSubjectsUseDifferentUnits_ThenEachUnitIsRead()
    {
        // Arrange
        using var server = new ModbusTestServer(1, 2);
        server.Start();
        SeedServer(server);
        server.SetHoldingRegister<ushort>(0, 99, unitId: 2);

        // Act
        var (device, source, recorder) = await StartAsync(server, testDevice => testDevice.Second = new SecondUnit());
        try
        {
            // Assert
            Assert.Equal(21.5m, device.Temperature);
            Assert.Equal(99, device.Second!.Value);
        }
        finally
        {
            recorder.Dispose();
            await source.DisposeAsync();
        }
    }

    [Fact]
    public async Task WhenServerRestarts_ThenSourceReconnectsAndRunsDiscoveryAgain()
    {
        // Arrange
        using var server = new ModbusTestServer();
        server.Start();
        SeedServer(server);
        var (device, source, recorder) = await StartAsync(server);
        try
        {
            // Act
            server.Stop();
            await recorder.WaitForStatesAsync(TimeSpan.FromSeconds(30), "The outage should be reported.",
                SourceState.Synchronized, SourceState.Synchronizing);
            server.Start();
            SeedServer(server);
            server.SetHoldingRegister<ushort>(1, 77);

            // Assert
            await recorder.WaitForStatesAsync(TimeSpan.FromSeconds(30), "The source should recover.",
                SourceState.Synchronized, SourceState.Synchronizing, SourceState.Synchronized);
            await AsyncTestHelpers.WaitUntilAsync(() => device.Counter == 77, TimeSpan.FromSeconds(10), message: "Counter should update after the reconnect.");
            Assert.True(device.DiscoveryCount >= 2);
        }
        finally
        {
            recorder.Dispose();
            await source.DisposeAsync();
        }
    }

    [Fact]
    public async Task WhenKillFaultIsInjected_ThenSourceRecovers()
    {
        // Arrange
        using var server = new ModbusTestServer();
        server.Start();
        SeedServer(server);
        var (_, source, recorder) = await StartAsync(server);
        try
        {
            // The kill only acts inside a poll attempt, so wait until the poll loop runs.
            await AsyncTestHelpers.WaitUntilAsync(() => source.Diagnostics.Polling.TotalPolls >= 2, TimeSpan.FromSeconds(10), message: "Polling should start.");

            // Act
            await ((IFaultInjectable)source).InjectFaultAsync(FaultType.Kill, CancellationToken.None);

            // Assert
            await recorder.WaitForStatesAsync(TimeSpan.FromSeconds(30), "The source should recover from the kill.",
                SourceState.Synchronized, SourceState.Synchronizing, SourceState.Synchronized);
        }
        finally
        {
            recorder.Dispose();
            await source.DisposeAsync();
        }
    }

    [Fact]
    public async Task WhenDisconnectFaultIsInjected_ThenSourceReconnectsAndRunsDiscoveryAgain()
    {
        // Arrange
        using var server = new ModbusTestServer();
        server.Start();
        SeedServer(server);
        var (device, source, recorder) = await StartAsync(server);
        try
        {
            // Act
            await ((IFaultInjectable)source).InjectFaultAsync(FaultType.Disconnect, CancellationToken.None);

            // Assert
            await recorder.WaitForStatesAsync(TimeSpan.FromSeconds(30), "The source should recover from the disconnect.",
                SourceState.Synchronized, SourceState.Synchronizing, SourceState.Synchronized);
            Assert.True(device.DiscoveryCount >= 2);
        }
        finally
        {
            recorder.Dispose();
            await source.DisposeAsync();
        }
    }

    [Fact]
    public async Task WhenSourceIsSynchronized_ThenDiagnosticsReportOperationalStateAndClaims()
    {
        // Arrange
        using var server = new ModbusTestServer();
        server.Start();
        SeedServer(server);

        // Act
        var (_, source, recorder) = await StartAsync(server);
        try
        {
            // Assert
            Assert.True(source.Diagnostics.IsOperational);
            Assert.Equal(6, source.Diagnostics.ClaimedPropertyCount);
            await AsyncTestHelpers.WaitUntilAsync(() => source.Diagnostics.Polling.TotalPolls >= 2, TimeSpan.FromSeconds(10), message: "Polling should continue.");
        }
        finally
        {
            recorder.Dispose();
            await source.DisposeAsync();
        }
    }

    [Fact]
    public async Task WhenSourceIsDisposed_ThenClaimedPropertiesAreReleased()
    {
        // Arrange
        using var server = new ModbusTestServer();
        server.Start();
        SeedServer(server);
        var (device, source, recorder) = await StartAsync(server);
        var counter = new PropertyReference(device, nameof(TestDevice.Counter));
        Assert.True(counter.TryGetSource(out _));

        // Act
        recorder.Dispose();
        await source.DisposeAsync();

        // Assert
        Assert.False(counter.TryGetSource(out _));
        Assert.False(source.Diagnostics.IsOperational);
    }

    [Fact]
    public async Task WhenSourceIsDisposedSynchronously_ThenClaimsAreReleasedAndPollingStops()
    {
        // Arrange
        using var server = new ModbusTestServer();
        server.Start();
        SeedServer(server);
        var (device, source, recorder) = await StartAsync(server);
        var counter = new PropertyReference(device, nameof(TestDevice.Counter));
        await AsyncTestHelpers.WaitUntilAsync(() => source.Diagnostics.Polling.TotalPolls >= 2, TimeSpan.FromSeconds(10), message: "Polling should start.");
        var executeTask = source.ExecuteTask;
        Assert.NotNull(executeTask);

        // Act
        recorder.Dispose();
        ((IDisposable)source).Dispose();

        // Assert (the poll loop ends before the execution does)
        Assert.False(counter.TryGetSource(out _));
        Assert.Equal(0, source.Diagnostics.ClaimedPropertyCount);
        await executeTask.WaitAsync(TimeSpan.FromSeconds(10));
        await AsyncTestHelpers.WaitUntilAsync(() => server.ConnectionCount == 0, TimeSpan.FromSeconds(10), message: "The connection should be closed.");
    }

    [Fact]
    public async Task WhenServerIsUnreachableAtStart_ThenSourceSynchronizesOnceItAppears()
    {
        // Arrange
        using var server = new ModbusTestServer();
        var device = CreateDevice();
        var source = CreateSource(device, server);
        using var recorder = SourceStateRecorder.SubscribeTo(source);
        try
        {
            await source.StartAsync(CancellationToken.None);
            await AsyncTestHelpers.WaitUntilAsync(() => source.Diagnostics.LastError is not null, TimeSpan.FromSeconds(10), message: "The first connect should fail.");

            // Act
            server.Start();
            SeedServer(server);

            // Assert
            await recorder.WaitForStatesAsync(TimeSpan.FromSeconds(30), "The source should synchronize.", SourceState.Synchronized);
            // A retry can load between Start and SeedServer, so the seeded value may arrive with a later poll.
            await AsyncTestHelpers.WaitUntilAsync(() => device.Counter == 42, TimeSpan.FromSeconds(10), message: "Counter should load.");
            Assert.True(source.Diagnostics.IsOperational);
        }
        finally
        {
            await source.DisposeAsync();
        }
    }

    [Fact]
    public async Task WhenDiscoveryFailsOnce_ThenTheRetrySynchronizes()
    {
        // Arrange
        using var server = new ModbusTestServer();
        server.Start();
        SeedServer(server);

        // Act
        var (device, source, recorder) = await StartAsync(server, testDevice => testDevice.OnDiscover = (_, _) =>
            testDevice.DiscoveryCount == 1
                ? Task.FromException(new InvalidOperationException("Discovery failed once."))
                : Task.CompletedTask);
        try
        {
            // Assert
            Assert.Equal(2, device.DiscoveryCount);
            Assert.Equal(42, device.Counter);
            Assert.IsType<InvalidOperationException>(source.Diagnostics.LastError);
        }
        finally
        {
            recorder.Dispose();
            await source.DisposeAsync();
        }
    }

    [Fact]
    public async Task WhenReconnectExcludesAPreviouslyClaimedProperty_ThenItIsReleased()
    {
        // Arrange
        using var server = new ModbusTestServer();
        server.Start();
        SeedServer(server);
        var (device, source, recorder) = await StartAsync(server, testDevice => testDevice.OnDiscover = (context, _) =>
        {
            if (testDevice.DiscoveryCount > 1)
            {
                context.ExcludeProperty(new PropertyReference(testDevice, nameof(TestDevice.Optional)));
            }

            return Task.CompletedTask;
        });
        var optional = new PropertyReference(device, nameof(TestDevice.Optional));
        try
        {
            Assert.True(optional.TryGetSource(out _));

            // Act
            await ((IFaultInjectable)source).InjectFaultAsync(FaultType.Disconnect, CancellationToken.None);

            // Assert
            await recorder.WaitForStatesAsync(TimeSpan.FromSeconds(30), "The source should recover from the disconnect.",
                SourceState.Synchronized, SourceState.Synchronizing, SourceState.Synchronized);
            Assert.False(optional.TryGetSource(out _));
            Assert.Equal(5, source.Diagnostics.ClaimedPropertyCount);
        }
        finally
        {
            recorder.Dispose();
            await source.DisposeAsync();
        }
    }

    [Fact]
    public async Task WhenPropertyIsOwnedByAnotherSource_ThenItIsNeitherClaimedNorRead()
    {
        // Arrange
        using var server = new ModbusTestServer();
        server.Start();
        SeedServer(server);
        var device = CreateDevice();
        var optional = new PropertyReference(device, nameof(TestDevice.Optional));
        await using var otherSource = CreateSource(device, server);
        Assert.True(optional.SetSource(otherSource));
        var source = CreateSource(device, server);
        using var recorder = SourceStateRecorder.SubscribeTo(source);
        try
        {
            // Act
            await source.StartAsync(CancellationToken.None);
            await recorder.WaitForStatesAsync(TimeSpan.FromSeconds(30), "The source should synchronize.", SourceState.Synchronized);

            // Assert
            Assert.Null(device.Optional);
            Assert.True(optional.TryGetSource(out var owner));
            Assert.Same(otherSource, owner);
            Assert.Equal(5, source.Diagnostics.ClaimedPropertyCount);
            Assert.DoesNotContain(server.Requests, request =>
                request.FunctionCode == ModbusFunctionCode.ReadHoldingRegisters &&
                request.Address <= 20 && request.Address + request.Quantity > 20);
        }
        finally
        {
            await source.DisposeAsync();
        }
    }
}
