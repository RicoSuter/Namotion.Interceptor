using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using FluentModbus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Monitoring;
using Namotion.Interceptor.Modbus.Attributes;
using Namotion.Interceptor.Modbus.Client;
using Namotion.Interceptor.Modbus.Tests.Testing;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Modbus.Tests.Client;

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

        [ModbusRegister(10, ModbusDataType.U32, AddressSpace = ModbusAddressSpace.InputRegister)]
        public partial long? Energy { get; set; }

        [ModbusRegister(3, ModbusDataType.Boolean, AddressSpace = ModbusAddressSpace.Coil)]
        public partial bool? Pump { get; set; }

        [ModbusRegister(1, ModbusDataType.Boolean, AddressSpace = ModbusAddressSpace.DiscreteInput)]
        public partial bool? Alarm { get; set; }

        [ModbusRegister(20, ModbusDataType.U16)]
        public partial int? Optional { get; set; }

        public partial SecondUnit? Second { get; set; }

        public partial DiscoveredChild? Discovered { get; set; }

        public Func<ModbusDiscoveryContext, CancellationToken, Task>? OnDiscover { get; set; }

        public int DiscoveryCount => Volatile.Read(ref _discoveryCount);

        public Task DiscoverAsync(ModbusDiscoveryContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _discoveryCount);
            return OnDiscover?.Invoke(context, cancellationToken) ?? Task.CompletedTask;
        }
    }

    [InterceptorSubject]
    public partial class SecondUnit : IModbusUnitIdProvider
    {
        public byte UnitId => 2;

        [ModbusRegister(0, ModbusDataType.U16)]
        public partial int? Value { get; set; }
    }

    [InterceptorSubject]
    public partial class DiscoveredChild
    {
        [ModbusRegister(30, ModbusDataType.U16)]
        public partial int? Value { get; set; }
    }

    [InterceptorSubject]
    public partial class ScaledDevice
    {
        [ModbusRegister(40, ModbusDataType.U16, ScaleFactorProperty = nameof(Factor))]
        public partial decimal? Scaled { get; set; }

        [ModbusRegister(41, ModbusDataType.S16)]
        public partial short? Factor { get; set; }

        [ModbusRegister(42, ModbusDataType.U16)]
        public partial int? Other { get; set; }
    }

    [InterceptorSubject]
    public partial class ScaledBlock
    {
        [ModbusRegister(0, ModbusDataType.S16)]
        public partial short? CurrentScaleFactor { get; set; }

        public partial ScaledChannel? Channel { get; set; }
    }

    [InterceptorSubject]
    public partial class ScaledChannel : IModbusBaseAddressProvider, IModbusScaleFactorProvider
    {
        public ScaledChannel(ScaledBlock block)
        {
            Block = block;
        }

        public ScaledBlock Block { get; }

        public int BaseAddress => 10;

        [ModbusRegister(0, ModbusDataType.U16)]
        public partial decimal? Current { get; set; }

        public PropertyReference? TryGetScaleFactorProperty(string propertyName)
            => propertyName == nameof(Current) ? new PropertyReference(Block, nameof(ScaledBlock.CurrentScaleFactor)) : null;
    }

    [InterceptorSubject]
    public partial class LongStringDevice
    {
        [ModbusRegister(100, ModbusDataType.String, Length = 150)]
        public partial string? Text { get; set; }
    }

    private static void SetString(ModbusTestServer server, int address, int length, string text)
    {
        for (var register = 0; register < length; register++)
        {
            var high = register * 2 < text.Length ? text[register * 2] : '\0';
            var low = register * 2 + 1 < text.Length ? text[register * 2 + 1] : '\0';
            server.SetHoldingRegister(address + register, (ushort)((high << 8) | low));
        }
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

    private static ModbusSubjectClientSource CreateSource(IInterceptorSubject subject, ModbusTestServer server, ILogger? logger = null)
        => subject.CreateModbusClientSource(
            new ModbusClientConfiguration
            {
                Host = "127.0.0.1",
                Port = server.Port,
                PollingInterval = TimeSpan.FromMilliseconds(100),
                RetryTime = TimeSpan.FromMilliseconds(200),
                RequestTimeout = TimeSpan.FromSeconds(2)
            },
            logger ?? NullLogger.Instance);

    private static async Task<(TestDevice Device, ModbusSubjectClientSource Source, SourceStateRecorder Recorder)> StartAsync(
        ModbusTestServer server, Action<TestDevice>? configure = null, ILogger? logger = null)
    {
        var device = CreateDevice(configure);
        var source = CreateSource(device, server, logger);

        var recorder = SourceStateRecorder.SubscribeTo(source);
        try
        {
            await source.StartAsync(CancellationToken.None);
            await recorder.WaitForStatesAsync(TimeSpan.FromSeconds(30), "The source should synchronize.", SourceState.Synchronized);
            return (device, source, recorder);
        }
        catch
        {
            recorder.Dispose();
            await source.DisposeAsync();
            throw;
        }
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
            var pollsAfterRestore = source.Diagnostics.Polling.TotalPolls;
            await AsyncTestHelpers.WaitUntilAsync(() => source.Diagnostics.Polling.TotalPolls >= pollsAfterRestore + 2, TimeSpan.FromSeconds(10),
                message: "Polling should continue.");
            Assert.All(server.Requests, request => Assert.Contains(request.FunctionCode, ReadFunctionCodes));

            // A failed write would park the change for retry or drop it; the read-only source reports success instead.
            Assert.Equal(0, source.Diagnostics.OutboundRetries.Depth);
            Assert.Equal(0, source.Diagnostics.OutboundRetries.TotalDropped);
        }
        finally
        {
            recorder.Dispose();
            await source.DisposeAsync();
        }
    }

    [Fact]
    public async Task WhenPropertyIsWrittenLocallyAgain_ThenTheWarningIsLoggedOncePerConnection()
    {
        // Arrange
        using var server = new ModbusTestServer();
        server.Start();
        SeedServer(server);
        var logger = new RecordingLogger();
        var (device, source, recorder) = await StartAsync(server, logger: logger);
        try
        {
            // Act
            await WriteLocallyAndWaitForRestoreAsync(device, 998);
            await WriteLocallyAndWaitForRestoreAsync(device, 999);
            var warningsBeforeReconnect = CountWriteWarnings(logger);

            await ((IFaultInjectable)source).InjectFaultAsync(FaultType.Disconnect, CancellationToken.None);
            await recorder.WaitForStatesAsync(TimeSpan.FromSeconds(30), "The source should recover from the disconnect.",
                SourceState.Synchronized, SourceState.Synchronizing, SourceState.Synchronized);
            await WriteLocallyAndWaitForRestoreAsync(device, 999);

            // Assert
            Assert.Equal(1, warningsBeforeReconnect);
            Assert.Equal(2, CountWriteWarnings(logger));
        }
        finally
        {
            recorder.Dispose();
            await source.DisposeAsync();
        }

        static async Task WriteLocallyAndWaitForRestoreAsync(TestDevice device, int value)
        {
            device.Counter = value;
            await AsyncTestHelpers.WaitUntilAsync(() => device.Counter == 42, TimeSpan.FromSeconds(10), message: "The device value should be restored.");
        }

        static int CountWriteWarnings(RecordingLogger logger)
            => logger.Warnings.Count(warning => warning.Contains(nameof(TestDevice.Counter)) && warning.Contains("cannot be written"));
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
            Assert.Equal(1, source.Diagnostics.Polling.UnavailablePropertyCount);
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
    public async Task WhenReconnectingAfterARegisterWasRejected_ThenItIsReadAgain()
    {
        // Arrange
        using var server = new ModbusTestServer();
        server.Start();
        SeedServer(server);
        server.RejectAddress(ModbusAddressSpace.HoldingRegister, 1);
        var (device, source, recorder) = await StartAsync(server);
        try
        {
            Assert.Equal(1, source.Diagnostics.Polling.UnavailablePropertyCount);
            server.AcceptAllAddresses();

            // Act
            await ((IFaultInjectable)source).InjectFaultAsync(FaultType.Disconnect, CancellationToken.None);

            // Assert
            await recorder.WaitForStatesAsync(TimeSpan.FromSeconds(30), "The source should recover from the disconnect.",
                SourceState.Synchronized, SourceState.Synchronizing, SourceState.Synchronized);
            Assert.Equal(42, device.Counter);
            Assert.Equal(0, source.Diagnostics.Polling.UnavailablePropertyCount);
        }
        finally
        {
            recorder.Dispose();
            await source.DisposeAsync();
        }
    }

    [Fact]
    public async Task WhenDiscoveryAddsAndLaterRemovesAChildSubject_ThenItsRegistersAreReadAndThenReleased()
    {
        // Arrange
        using var server = new ModbusTestServer();
        server.Start();
        SeedServer(server);
        server.SetHoldingRegister<ushort>(30, 9);
        var isChildActive = new StrongBox<bool>(true);
        var (device, source, recorder) = await StartAsync(server, testDevice => testDevice.OnDiscover = (_, _) =>
        {
            testDevice.Discovered = isChildActive.Value ? testDevice.Discovered ?? new DiscoveredChild() : null;
            return Task.CompletedTask;
        });
        var child = device.Discovered;
        var initialValue = child?.Value;
        try
        {
            // Act
            isChildActive.Value = false;
            await ((IFaultInjectable)source).InjectFaultAsync(FaultType.Disconnect, CancellationToken.None);
            await recorder.WaitForStatesAsync(TimeSpan.FromSeconds(30), "The source should recover from the disconnect.",
                SourceState.Synchronized, SourceState.Synchronizing, SourceState.Synchronized);
            server.SetHoldingRegister<ushort>(30, 10);
            var pollsAfterChange = source.Diagnostics.Polling.TotalPolls;
            await AsyncTestHelpers.WaitUntilAsync(() => source.Diagnostics.Polling.TotalPolls >= pollsAfterChange + 2, TimeSpan.FromSeconds(10),
                message: "Polling should continue after the child is removed.");

            // Assert
            Assert.NotNull(child);
            Assert.Equal(9, initialValue);
            Assert.True(device.DiscoveryCount >= 2);
            Assert.Null(device.Discovered);
            Assert.False(new PropertyReference(child, nameof(DiscoveredChild.Value)).TryGetSource(out _));
            Assert.Equal(9, child.Value);
        }
        finally
        {
            recorder.Dispose();
            await source.DisposeAsync();
        }
    }

    [Fact]
    public async Task WhenChildSubjectIsDetachedWhilePolling_ThenItsValuesAreNoLongerWritten()
    {
        // Arrange
        using var server = new ModbusTestServer();
        server.Start();
        SeedServer(server);
        server.SetHoldingRegister<ushort>(30, 9);
        var (device, source, recorder) = await StartAsync(server, testDevice => testDevice.Discovered = new DiscoveredChild());
        var child = device.Discovered;
        var initialValue = child?.Value;
        try
        {
            // Act
            device.Discovered = null;
            server.SetHoldingRegister<ushort>(30, 10);
            var pollsAfterChange = source.Diagnostics.Polling.TotalPolls;
            await AsyncTestHelpers.WaitUntilAsync(() => source.Diagnostics.Polling.TotalPolls >= pollsAfterChange + 2, TimeSpan.FromSeconds(10),
                message: "Polling should continue after the child is detached.");

            // Assert
            Assert.NotNull(child);
            Assert.Equal(9, initialValue);
            Assert.False(new PropertyReference(child, nameof(DiscoveredChild.Value)).TryGetSource(out _));
            Assert.Equal(9, child.Value);
        }
        finally
        {
            recorder.Dispose();
            await source.DisposeAsync();
        }
    }

    [Fact]
    public async Task WhenDetachedPropertyIsClaimedByAnotherSource_ThenPollingDoesNotOverwriteIt()
    {
        // Arrange
        using var server = new ModbusTestServer();
        server.Start();
        SeedServer(server);
        server.SetHoldingRegister<ushort>(30, 9);
        var (device, source, recorder) = await StartAsync(server, testDevice => testDevice.Discovered = new DiscoveredChild());
        var child = device.Discovered!;
        var value = new PropertyReference(child, nameof(DiscoveredChild.Value));
        var otherSource = Mock.Of<ISubjectSource>();
        try
        {
            // Act
            device.Discovered = null;
            Assert.True(value.SetSource(otherSource));
            child.Value = 5;
            server.SetHoldingRegister<ushort>(30, 10);
            var pollsAfterChange = source.Diagnostics.Polling.TotalPolls;
            await AsyncTestHelpers.WaitUntilAsync(() => source.Diagnostics.Polling.TotalPolls >= pollsAfterChange + 2, TimeSpan.FromSeconds(10),
                message: "Polling should continue after the child is detached.");

            // Assert
            Assert.Equal(5, child.Value);
            Assert.True(value.TryGetSource(out var owner));
            Assert.Same(otherSource, owner);
        }
        finally
        {
            recorder.Dispose();
            await source.DisposeAsync();
        }
    }

    [Fact]
    public async Task WhenSourceReconnects_ThenItIsOperationalBeforeItSynchronizes()
    {
        // Arrange
        using var server = new ModbusTestServer();
        server.Start();
        SeedServer(server);
        var (_, source, recorder) = await StartAsync(server);
        var operationalWhenSynchronized = new ConcurrentQueue<bool?>();
        void OnStateChanged(object? sender, SourceEvent sourceEvent)
        {
            if (sourceEvent.NewState == SourceState.Synchronized)
            {
                operationalWhenSynchronized.Enqueue(source.Diagnostics.IsOperational);
            }
        }

        source.StateChanged += OnStateChanged;
        try
        {
            // Act
            await ((IFaultInjectable)source).InjectFaultAsync(FaultType.Disconnect, CancellationToken.None);

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(() => !operationalWhenSynchronized.IsEmpty, TimeSpan.FromSeconds(30),
                message: "The source should synchronize again after the disconnect.");
            Assert.All(operationalWhenSynchronized, isOperational => Assert.True(isOperational));
        }
        finally
        {
            source.StateChanged -= OnStateChanged;
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
        var wasClaimed = counter.TryGetSource(out _);

        // Act
        recorder.Dispose();
        await source.DisposeAsync();

        // Assert
        Assert.True(wasClaimed);
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
        Task? executeTask;
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(() => source.Diagnostics.Polling.TotalPolls >= 2, TimeSpan.FromSeconds(10), message: "Polling should start.");
            executeTask = source.ExecuteTask;
            Assert.NotNull(executeTask);
        }
        catch
        {
            recorder.Dispose();
            await source.DisposeAsync();
            throw;
        }

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
    public async Task WhenSourceIsDisposedDuringAReconnect_ThenClaimsAreReleasedAndTheConnectionIsClosed()
    {
        // Arrange
        using var server = new ModbusTestServer();
        server.Start();
        SeedServer(server);
        var logger = new RecordingLogger();
        var reconnectDiscoveryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (_, source, recorder) = await StartAsync(server, testDevice => testDevice.OnDiscover = async (context, cancellationToken) =>
        {
            if (testDevice.DiscoveryCount > 1)
            {
                // The test server only notices a closed client that has sent a request.
                await context.ReadHoldingRegistersAsync(0, 1, cancellationToken: cancellationToken);
                reconnectDiscoveryStarted.TrySetResult();
                // Blocks the reconnect inside discovery until the disposal cancels it.
                await new TaskCompletionSource().Task.WaitAsync(cancellationToken);
            }
        }, logger);
        try
        {
            await ((IFaultInjectable)source).InjectFaultAsync(FaultType.Disconnect, CancellationToken.None);
            await reconnectDiscoveryStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch
        {
            recorder.Dispose();
            await source.DisposeAsync();
            throw;
        }

        // Act
        recorder.Dispose();
        await source.DisposeAsync();

        // Assert
        Assert.Equal(0, source.Diagnostics.ClaimedPropertyCount);
        await AsyncTestHelpers.WaitUntilAsync(() => server.ConnectionCount == 0, TimeSpan.FromSeconds(10), message: "The connection should be closed.");
        Assert.DoesNotContain(logger.Errors, error => error.Contains("owned by another source"));
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
        var (device, source, recorder) = await StartAsync(server, testDevice => testDevice.OnDiscover = async (context, cancellationToken) =>
        {
            if (testDevice.DiscoveryCount == 1)
            {
                // The test server only notices a closed client that has sent a request.
                await context.ReadHoldingRegistersAsync(0, 1, cancellationToken: cancellationToken);
                throw new InvalidOperationException("Discovery failed once.");
            }
        });
        try
        {
            // Assert
            Assert.Equal(2, device.DiscoveryCount);
            Assert.Equal(42, device.Counter);
            Assert.IsType<InvalidOperationException>(source.Diagnostics.LastError);
            await AsyncTestHelpers.WaitUntilAsync(() => server.ConnectionCount == 1, TimeSpan.FromSeconds(10),
                message: "The failed attempt's connection should be closed.");
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
        var wasClaimed = optional.TryGetSource(out _);
        try
        {
            // Act
            await ((IFaultInjectable)source).InjectFaultAsync(FaultType.Disconnect, CancellationToken.None);
            await recorder.WaitForStatesAsync(TimeSpan.FromSeconds(30), "The source should recover from the disconnect.",
                SourceState.Synchronized, SourceState.Synchronizing, SourceState.Synchronized);

            // Assert
            Assert.True(wasClaimed);
            Assert.False(optional.TryGetSource(out _));
            Assert.Equal(7, device.Optional);
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
        var otherSource = Mock.Of<ISubjectSource>();
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

    [Fact]
    public async Task WhenScaleFactorIsOwnedByAnotherSource_ThenItsDependentIsNeitherClaimedNorRead()
    {
        // Arrange
        using var server = new ModbusTestServer();
        server.Start();
        server.SetHoldingRegister<ushort>(40, 123);
        server.SetHoldingRegister<short>(41, -1);
        server.SetHoldingRegister<ushort>(42, 7);
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var device = new ScaledDevice(context);
        var factor = new PropertyReference(device, nameof(ScaledDevice.Factor));
        var scaled = new PropertyReference(device, nameof(ScaledDevice.Scaled));
        var otherSource = Mock.Of<ISubjectSource>();
        Assert.True(factor.SetSource(otherSource));
        var logger = new RecordingLogger();
        var source = CreateSource(device, server, logger);
        using var recorder = SourceStateRecorder.SubscribeTo(source);
        try
        {
            // Act
            await source.StartAsync(CancellationToken.None);
            await recorder.WaitForStatesAsync(TimeSpan.FromSeconds(30), "The source should synchronize.", SourceState.Synchronized);

            // Assert
            Assert.Equal(7, device.Other);
            Assert.Null(device.Scaled);
            Assert.False(scaled.TryGetSource(out _));
            Assert.Equal(1, source.Diagnostics.ClaimedPropertyCount);
            Assert.Single(logger.Errors, error =>
                error.Contains(nameof(ScaledDevice.Scaled)) && error.Contains(nameof(ScaledDevice.Factor)));
            Assert.DoesNotContain(server.Requests, request =>
                request.FunctionCode == ModbusFunctionCode.ReadHoldingRegisters &&
                request.Address <= 40 && request.Address + request.Quantity > 40);
        }
        finally
        {
            await source.DisposeAsync();
        }
    }

    [Fact]
    public async Task WhenStringIsLongerThanOneRequest_ThenItIsReadInConsecutiveRequestsAndUpdated()
    {
        // Arrange
        using var server = new ModbusTestServer();
        server.Start();
        var initial = new string('A', 300);
        var changed = new string('A', 260) + "Changed";
        SetString(server, 100, 150, initial);

        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry().WithLifecycle();
        var device = new LongStringDevice(context);
        var source = CreateSource(device, server);
        try
        {
            // Act
            await source.StartAsync(CancellationToken.None);
            await AsyncTestHelpers.WaitUntilAsync(() => device.Text == initial, TimeSpan.FromSeconds(30), message: "The long string should be loaded.");
            SetString(server, 100, 150, changed);

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(() => device.Text == changed, TimeSpan.FromSeconds(10), message: "The long string should update.");
            Assert.All(
                server.Requests.Where(request => request.FunctionCode == ModbusFunctionCode.ReadHoldingRegisters),
                request => Assert.Contains((request.Address, request.Quantity), new[] { (100, 125), (225, 25) }));
        }
        finally
        {
            await source.DisposeAsync();
        }
    }

    [Fact]
    public async Task WhenScaleFactorIsProvidedByAnotherSubject_ThenValueIsScaled()
    {
        // Arrange
        using var server = new ModbusTestServer();
        server.Start();
        server.SetHoldingRegister<short>(0, -1);
        server.SetHoldingRegister<ushort>(10, 1234);

        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry().WithLifecycle();
        var block = new ScaledBlock(context);
        block.Channel = new ScaledChannel(block);
        var source = CreateSource(block, server);
        try
        {
            // Act
            await source.StartAsync(CancellationToken.None);

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(() => block.Channel.Current == 123.4m, TimeSpan.FromSeconds(30), message: "The channel should be scaled by the block's scale factor.");
        }
        finally
        {
            await source.DisposeAsync();
        }
    }
}
