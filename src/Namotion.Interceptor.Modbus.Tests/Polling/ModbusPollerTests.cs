using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Modbus.Attributes;
using Namotion.Interceptor.Modbus.Mapping;
using Namotion.Interceptor.Modbus.Polling;
using Namotion.Interceptor.Modbus.Tests.Testing;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Modbus.Tests.Polling;

public partial class ModbusPollerTests
{
    [InterceptorSubject]
    public partial class PollerSubject
    {
        [ModbusRegister(0, ModbusDataType.U16)]
        public partial int? First { get; set; }

        [ModbusRegister(1, ModbusDataType.S16, Scale = 0.1)]
        public partial decimal? Second { get; set; }

        [ModbusRegister(2, ModbusDataType.U16, ScaleFactorProperty = nameof(Factor))]
        public partial decimal? Scaled { get; set; }

        [ModbusRegister(3, ModbusDataType.S16)]
        public partial short? Factor { get; set; }

        [ModbusRegister(0, ModbusDataType.Boolean, AddressSpace = ModbusAddressSpace.Coil)]
        public partial bool? Pump { get; set; }
    }

    [InterceptorSubject]
    public partial class GapSubject
    {
        [ModbusRegister(0, ModbusDataType.U16)]
        public partial int? First { get; set; }

        [ModbusRegister(2, ModbusDataType.U16)]
        public partial int? Second { get; set; }
    }

    [InterceptorSubject]
    public partial class SeparateScaleFactorSubject
    {
        [ModbusRegister(0, ModbusDataType.S16)]
        public partial short? Factor { get; set; }

        [ModbusRegister(10, ModbusDataType.U16, ScaleFactorProperty = nameof(Factor))]
        public partial decimal? Scaled { get; set; }
    }

    [InterceptorSubject]
    public partial class NotAvailableScaleFactorSubject
    {
        [ModbusRegister(0, ModbusDataType.S16, NotAvailableValue = ModbusNotAvailableValue.SignedMinimum)]
        public partial short? Factor { get; set; }

        [ModbusRegister(1, ModbusDataType.U16, ScaleFactorProperty = nameof(Factor))]
        public partial decimal? Scaled { get; set; }
    }

    private static IInterceptorSubjectContext CreateContext()
        => InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry().WithLifecycle();

    private static (ModbusPoller Poller, FakeRegisterReader Reader, ModbusPollingMetrics Metrics) Create(ILogger? logger = null)
    {
        var subject = new PollerSubject(CreateContext());
        var bindings = ModbusRegisterResolver.Resolve(subject, 1, new HashSet<PropertyReference>());
        var metrics = new ModbusPollingMetrics();
        var reader = new FakeRegisterReader();
        reader.SetRegister(0, 42);
        reader.SetRegister(1, 215);
        reader.SetRegister(2, 123);
        reader.SetRegister(3, unchecked((ushort)-1));
        reader.SetBit(0, true);
        return (new ModbusPoller(bindings, 0, metrics, logger ?? NullLogger.Instance), reader, metrics);
    }

    private static Dictionary<string, object?> Apply(ModbusPoller poller)
    {
        var applied = new Dictionary<string, object?>();
        poller.ApplyChanges(applied, static (state, property, value) => state[property.Name] = value);
        return applied;
    }

    [Fact]
    public async Task WhenFirstCycleCompletes_ThenAllValuesAreApplied()
    {
        // Arrange
        var (poller, reader, _) = Create();

        // Act
        await poller.ReadAsync(reader, CancellationToken.None);
        var applied = Apply(poller);

        // Assert
        Assert.Equal(42, applied["First"]);
        Assert.Equal(21.5m, applied["Second"]);
        Assert.Equal(12.3m, applied["Scaled"]);
        Assert.Equal((short)-1, applied["Factor"]);
        Assert.True(Assert.IsType<bool>(applied["Pump"]));
    }

    [Fact]
    public async Task WhenRawValuesAreUnchanged_ThenNothingIsApplied()
    {
        // Arrange
        var (poller, reader, _) = Create();
        await poller.ReadAsync(reader, CancellationToken.None);
        Apply(poller);

        // Act
        await poller.ReadAsync(reader, CancellationToken.None);
        var applied = Apply(poller);

        // Assert
        Assert.Empty(applied);
    }

    [Fact]
    public async Task WhenOneRegisterChanges_ThenOnlyThatPropertyIsApplied()
    {
        // Arrange
        var (poller, reader, _) = Create();
        await poller.ReadAsync(reader, CancellationToken.None);
        Apply(poller);
        reader.SetRegister(0, 43);

        // Act
        await poller.ReadAsync(reader, CancellationToken.None);
        var applied = Apply(poller);

        // Assert
        Assert.Equal(43, Assert.Single(applied).Value);
    }

    [Fact]
    public async Task WhenScaleFactorChanges_ThenDependentIsReapplied()
    {
        // Arrange
        var (poller, reader, _) = Create();
        await poller.ReadAsync(reader, CancellationToken.None);
        Apply(poller);
        reader.SetRegister(3, unchecked((ushort)-2));

        // Act
        await poller.ReadAsync(reader, CancellationToken.None);
        var applied = Apply(poller);

        // Assert
        Assert.Equal(1.23m, applied["Scaled"]);
        Assert.Equal((short)-2, applied["Factor"]);
        Assert.Equal(2, applied.Count);
    }

    [Fact]
    public async Task WhenScaleFactorChangesWhileDependentReadFails_ThenDependentIsReappliedOnItsNextRead()
    {
        // Arrange
        var subject = new SeparateScaleFactorSubject(CreateContext());
        var bindings = ModbusRegisterResolver.Resolve(subject, 1, new HashSet<PropertyReference>());
        var poller = new ModbusPoller(bindings, 0, new ModbusPollingMetrics(), NullLogger.Instance);
        var reader = new FakeRegisterReader();
        reader.SetRegister(0, unchecked((ushort)-1));
        reader.SetRegister(10, 123);
        await poller.ReadAsync(reader, CancellationToken.None);
        Apply(poller);

        reader.Reject(10);
        reader.SetRegister(0, unchecked((ushort)-2));
        await poller.ReadAsync(reader, CancellationToken.None);
        var failedCycle = Apply(poller);
        reader.Accept(10);

        // Act
        await poller.ReadAsync(reader, CancellationToken.None);
        var applied = Apply(poller);

        // Assert
        Assert.False(failedCycle.ContainsKey("Scaled"));
        Assert.Equal(1.23m, Assert.Single(applied).Value);
    }

    [Fact]
    public async Task WhenScaleFactorIsNotAvailable_ThenDependentIsNotUpdatedUntilItIsAvailableAgain()
    {
        // Arrange
        var subject = new NotAvailableScaleFactorSubject(CreateContext());
        var bindings = ModbusRegisterResolver.Resolve(subject, 1, new HashSet<PropertyReference>());
        var poller = new ModbusPoller(bindings, 0, new ModbusPollingMetrics(), NullLogger.Instance);
        var reader = new FakeRegisterReader();
        reader.SetRegister(0, unchecked((ushort)-1));
        reader.SetRegister(1, 123);
        await poller.ReadAsync(reader, CancellationToken.None);
        var availableCycle = Apply(poller);
        reader.SetRegister(0, 0x8000);
        reader.SetRegister(1, 124);

        // Act
        await poller.ReadAsync(reader, CancellationToken.None);
        var notAvailableCycle = Apply(poller);
        reader.SetRegister(0, unchecked((ushort)-1));
        await poller.ReadAsync(reader, CancellationToken.None);
        var availableAgainCycle = Apply(poller);

        // Assert
        Assert.Equal(12.3m, availableCycle["Scaled"]);
        Assert.Null(Assert.Single(notAvailableCycle, pair => pair.Key == "Factor").Value);
        Assert.False(notAvailableCycle.ContainsKey("Scaled"));
        Assert.Equal(12.4m, availableAgainCycle["Scaled"]);
    }

    [Fact]
    public async Task WhenConversionFails_ThenOtherValuesAreStillApplied()
    {
        // Arrange (a scale factor exponent of 29 is outside the decimal range)
        var (poller, reader, _) = Create();
        reader.SetRegister(3, 29);

        // Act
        await poller.ReadAsync(reader, CancellationToken.None);
        var applied = Apply(poller);

        // Assert
        Assert.False(applied.ContainsKey("Scaled"));
        Assert.Equal((short)29, applied["Factor"]);
        Assert.Equal(42, applied["First"]);
    }

    [Fact]
    public async Task WhenScaleFactorBecomesValidAfterConversionFailure_ThenUnchangedDependentIsApplied()
    {
        // Arrange
        var (poller, reader, _) = Create();
        reader.SetRegister(3, 29);
        await poller.ReadAsync(reader, CancellationToken.None);
        var failedCycle = Apply(poller);
        reader.SetRegister(3, unchecked((ushort)-1));

        // Act
        await poller.ReadAsync(reader, CancellationToken.None);
        var applied = Apply(poller);

        // Assert
        Assert.False(failedCycle.ContainsKey("Scaled"));
        Assert.Equal(12.3m, applied["Scaled"]);
    }

    [Fact]
    public async Task WhenPlanIsRebuiltWhileSingleMappingBatchStillFails_ThenItsWarningIsNotLoggedAgain()
    {
        // Arrange (the rejected holding register rebuilds the plan at the end of the first cycle)
        var subject = new PollerSubject(CreateContext());
        var bindings = ModbusRegisterResolver.Resolve(subject, 1, new HashSet<PropertyReference>());
        var logger = new RecordingLogger();
        var poller = new ModbusPoller(bindings, 0, new ModbusPollingMetrics(), logger);
        var reader = new FakeRegisterReader();
        reader.Reject(1);
        reader.Reject(0, ModbusAddressSpace.Coil);
        var batchesBeforeReplan = poller.Batches;

        // Act
        await poller.ReadAsync(reader, CancellationToken.None);
        await poller.ReadAsync(reader, CancellationToken.None);

        // Assert
        Assert.NotSame(batchesBeforeReplan, poller.Batches);
        Assert.Single(logger.Warnings, warning => warning.Contains(nameof(PollerSubject.Pump)));
    }

    [Fact]
    public async Task WhenReapplyIsRequested_ThenUnchangedValueIsApplied()
    {
        // Arrange
        var (poller, reader, _) = Create();
        await poller.ReadAsync(reader, CancellationToken.None);
        Apply(poller);
        poller.RequestReapply(poller.Bindings.Single(binding => binding.Property.Name == "First").Property);

        // Act
        await poller.ReadAsync(reader, CancellationToken.None);
        var applied = Apply(poller);

        // Assert
        Assert.Equal(42, Assert.Single(applied).Value);
    }

    [Fact]
    public async Task WhenBatchIsRejected_ThenMappingsAreReadOneByOneAndTheRejectedOneIsDropped()
    {
        // Arrange
        var (poller, reader, metrics) = Create();
        reader.Reject(1);

        // Act
        await poller.ReadAsync(reader, CancellationToken.None);
        var applied = Apply(poller);
        reader.Requests.Clear();
        await poller.ReadAsync(reader, CancellationToken.None);

        // Assert
        Assert.False(applied.ContainsKey("Second"));
        Assert.Equal(42, applied["First"]);
        Assert.Equal(12.3m, applied["Scaled"]);
        Assert.Equal(1, metrics.UnavailablePropertyCount);
        Assert.Equal(1, metrics.TotalFailedRequests);
        Assert.DoesNotContain(reader.Requests, request =>
            request.AddressSpace == ModbusAddressSpace.HoldingRegister && request.Address <= 1 && request.Address + request.Count > 1);
    }

    [Fact]
    public async Task WhenSingleMappingBatchIsRejected_ThenFailedBatchIsCountedAndOthersContinue()
    {
        // Arrange
        var (poller, reader, metrics) = Create();
        reader.Reject(0, ModbusAddressSpace.Coil);

        // Act
        await poller.ReadAsync(reader, CancellationToken.None);
        var applied = Apply(poller);

        // Assert
        Assert.False(applied.ContainsKey("Pump"));
        Assert.Equal(42, applied["First"]);
        Assert.Equal(1, metrics.TotalFailedRequests);
        Assert.Equal(0, metrics.UnavailablePropertyCount);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(10)]
    [InlineData(11)]
    public async Task WhenMultiMappingBatchFailsWithTransientCode_ThenItIsSkippedAndReadAsOneRequestAgain(int exceptionCode)
    {
        // Arrange
        var logger = new RecordingLogger();
        var (poller, reader, metrics) = Create(logger);
        reader.Reject(1, exceptionCode: exceptionCode);

        // Act
        await poller.ReadAsync(reader, CancellationToken.None);
        var failedCycle = Apply(poller);
        await poller.ReadAsync(reader, CancellationToken.None);
        Apply(poller);
        var failedCycleRequests = reader.Requests.ToArray();
        reader.Accept(1);
        reader.Requests.Clear();
        await poller.ReadAsync(reader, CancellationToken.None);
        var recoveredCycle = Apply(poller);

        // Assert
        Assert.Equal("Pump", Assert.Single(failedCycle).Key);
        Assert.Equal(4, failedCycleRequests.Length);
        Assert.Contains(((byte)1, ModbusAddressSpace.HoldingRegister, 0, 4), reader.Requests);
        Assert.Equal(2, reader.Requests.Count);
        Assert.Equal(21.5m, recoveredCycle["Second"]);
        Assert.Equal(42, recoveredCycle["First"]);
        Assert.Equal(2, metrics.TotalFailedRequests);
        Assert.Equal(2, metrics.BatchCount);
        Assert.Equal(0, metrics.UnavailablePropertyCount);
        Assert.Single(logger.Warnings);
    }

    [Fact]
    public async Task WhenMappingFailsWithTransientCodeWhileReadIndividually_ThenItIsNotMarkedUnavailable()
    {
        // Arrange (the gap register splits the batch, then the device is busy for Second)
        var subject = new GapSubject(CreateContext());
        var bindings = ModbusRegisterResolver.Resolve(subject, 1, new HashSet<PropertyReference>());
        var metrics = new ModbusPollingMetrics();
        var poller = new ModbusPoller(bindings, maximumRegisterGap: 1, metrics, NullLogger.Instance);
        var reader = new FakeRegisterReader();
        reader.SetRegister(0, 5);
        reader.SetRegister(2, 6);
        reader.Reject(1);
        reader.Reject(2, exceptionCode: 6);
        await poller.ReadAsync(reader, CancellationToken.None);
        var failedCycle = Apply(poller);
        reader.Accept(2);

        // Act
        await poller.ReadAsync(reader, CancellationToken.None);
        var recoveredCycle = Apply(poller);

        // Assert
        Assert.Equal(5, Assert.Single(failedCycle).Value);
        Assert.Equal(6, Assert.Single(recoveredCycle).Value);
        Assert.Equal(0, metrics.UnavailablePropertyCount);
        Assert.Equal(2, metrics.BatchCount);
    }

    [Fact]
    public async Task WhenSingleMappingBatchFailsWithTransientCode_ThenItIsCountedAndAppliedOnceDeviceAnswers()
    {
        // Arrange
        var (poller, reader, metrics) = Create();
        reader.Reject(0, ModbusAddressSpace.Coil, exceptionCode: 6);
        await poller.ReadAsync(reader, CancellationToken.None);
        var failedCycle = Apply(poller);
        reader.Accept(0, ModbusAddressSpace.Coil);

        // Act
        await poller.ReadAsync(reader, CancellationToken.None);
        var recoveredCycle = Apply(poller);

        // Assert
        Assert.False(failedCycle.ContainsKey("Pump"));
        Assert.True(Assert.IsType<bool>(Assert.Single(recoveredCycle).Value));
        Assert.Equal(1, metrics.TotalFailedRequests);
        Assert.Equal(0, metrics.UnavailablePropertyCount);
    }

    [Fact]
    public async Task WhenConnectionFails_ThenExceptionPropagates()
    {
        // Arrange
        var (poller, reader, _) = Create();
        reader.ConnectionFailure = new IOException("Connection reset");

        // Act & Assert
        await Assert.ThrowsAsync<IOException>(() => poller.ReadAsync(reader, CancellationToken.None));
    }

    [Fact]
    public async Task WhenConnectionFailsWhileReadingIndividually_ThenExceptionPropagatesAndNothingIsMarkedUnavailable()
    {
        // Arrange (request 0 is the rejected holding batch, request 1 reads First, request 2 reads Second)
        var (poller, reader, metrics) = Create();
        reader.Reject(1);
        reader.ConnectionFailure = new IOException("Connection reset");
        reader.ConnectionFailureFromRequest = 2;

        // Act & Assert
        await Assert.ThrowsAsync<IOException>(() => poller.ReadAsync(reader, CancellationToken.None));
        Assert.Equal(0, metrics.UnavailablePropertyCount);
    }

    [Fact]
    public async Task WhenGapAddressIsRejected_ThenLaterCyclesReadTheMappingsSeparately()
    {
        // Arrange
        var subject = new GapSubject(CreateContext());
        var bindings = ModbusRegisterResolver.Resolve(subject, 1, new HashSet<PropertyReference>());
        var metrics = new ModbusPollingMetrics();
        var poller = new ModbusPoller(bindings, maximumRegisterGap: 1, metrics, NullLogger.Instance);
        var reader = new FakeRegisterReader();
        reader.SetRegister(0, 5);
        reader.SetRegister(2, 6);
        reader.Reject(1);
        await poller.ReadAsync(reader, CancellationToken.None);
        var firstCycle = Apply(poller);
        reader.Requests.Clear();

        // Act
        await poller.ReadAsync(reader, CancellationToken.None);

        // Assert
        Assert.Equal(5, firstCycle["First"]);
        Assert.Equal(6, firstCycle["Second"]);
        Assert.Equal(2, reader.Requests.Count);
        Assert.All(reader.Requests, request => Assert.Equal(1, request.Count));
        Assert.Equal(1, metrics.TotalFailedRequests);
        Assert.Equal(2, metrics.BatchCount);
        Assert.Equal(0, metrics.UnavailablePropertyCount);
    }

    [Fact]
    public async Task WhenCycleCompletes_ThenMetricsAreRecorded()
    {
        // Arrange
        var (poller, reader, metrics) = Create();

        // Act
        await poller.ReadAsync(reader, CancellationToken.None);

        // Assert
        Assert.Equal(1, metrics.TotalPolls);
        Assert.NotNull(metrics.LastPollTime);
        Assert.Equal(2, metrics.BatchCount);
    }

    [Fact]
    public async Task WhenMetricsAreReset_ThenCountersAreClearedAndGaugesAreKept()
    {
        // Arrange
        var (poller, reader, metrics) = Create();
        reader.Reject(0, ModbusAddressSpace.Coil);
        await poller.ReadAsync(reader, CancellationToken.None);

        // Act
        metrics.Reset();

        // Assert
        Assert.Equal(0, metrics.TotalPolls);
        Assert.Equal(0, metrics.TotalFailedRequests);
        Assert.Equal(2, metrics.BatchCount);
        Assert.NotNull(metrics.LastPollTime);
        Assert.NotNull(metrics.LastPollDuration);
    }
}
