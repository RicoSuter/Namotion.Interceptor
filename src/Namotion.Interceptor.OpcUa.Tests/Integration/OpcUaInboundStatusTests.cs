using Namotion.Interceptor.Connectors.Monitoring;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Testing;
using Opc.Ua;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

/// <summary>
/// The rule every inbound path of the client shares: a value the server marked Bad is not usable and
/// must not reach the subject, while Good and Uncertain both are.
/// </summary>
[Trait("Category", "Integration")]
public class OpcUaInboundStatusTests
{
    private readonly ITestOutputHelper _output;

    public OpcUaInboundStatusTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task WhenASubscriptionNotificationIsBad_ThenTheValueIsNotApplied()
    {
        // Arrange
        await using var fixture = await InboundStatusFixture.StartAsync(_output);

        // Act
        fixture.PublishPair("from-faulted-sensor", StatusCodes.BadDeviceFailure, "sentinel", StatusCodes.Good);

        // Assert: the sibling is published second, and sequential publishing makes the queue order the
        // processing order, so its arrival proves the Bad value was already processed and skipped.
        await AsyncTestHelpers.WaitUntilAsync(
            () => fixture.ClientRoot.Child?.Other == "sentinel",
            message: "the notification carrying both properties should reach the client");

        Assert.Equal("initial", fixture.ClientRoot.Child!.Value);
    }

    [Fact]
    public async Task WhenASubscriptionNotificationIsBad_ThenTheSkipIsCounted()
    {
        // Arrange: a skip that is only logged at Debug leaves a permanently faulted sensor invisible,
        // so the count is what a monitoring consumer can see at the default log level.
        await using var fixture = await InboundStatusFixture.StartAsync(_output);
        var skippedBeforeTheNotification = fixture.SkippedBadSubscriptionValues;

        // Act
        fixture.PublishPair("from-faulted-sensor", StatusCodes.BadDeviceFailure, "sentinel", StatusCodes.Good);

        // Assert: at least one, because a server may resend the same Bad value.
        await AsyncTestHelpers.WaitUntilAsync(
            () => fixture.SkippedBadSubscriptionValues > skippedBeforeTheNotification,
            message: "the skipped Bad value should be counted on the client diagnostics");
    }

    [Fact]
    public async Task WhenOneValuesConversionThrows_ThenTheRestOfTheNotificationIsStillApplied()
    {
        // Arrange
        await using var fixture = await InboundStatusFixture.StartAsync(
            _output, valueConverter: new ThrowOnSentinelConverter("poison"));

        // Act
        fixture.PublishPair("poison", StatusCodes.Good, "survivor", StatusCodes.Good);

        // Assert: the pair is published in one lock hold and so normally arrives in one notification,
        // which is what makes the sibling evidence that a failing conversion did not abort the rest of
        // it. A split would make this pass trivially rather than fail, so it can only weaken the test.
        await AsyncTestHelpers.WaitUntilAsync(
            () => fixture.ClientRoot.Child?.Other == "survivor",
            message: "the sibling in the same notification should still be applied");

        Assert.Equal("initial", fixture.ClientRoot.Child!.Value);
    }

    [Fact]
    public async Task WhenANotificationCarriesNoSourceTimestamp_ThenTheRestOfItIsStillApplied()
    {
        // Arrange
        await using var fixture = await InboundStatusFixture.StartAsync(_output);
        OpcUaNodeStatusDriver.ClearSourceTimestamp(fixture.ServerService, fixture.ServerProperty);
        var beforeTheNotification = DateTimeOffset.UtcNow;

        // Act
        fixture.PublishPair("undated", StatusCodes.Good, "survivor", StatusCodes.Good);

        // Assert: the pair travels in one notification, so the sibling's arrival is what proves the
        // undated value did not take the whole notification down with it.
        await AsyncTestHelpers.WaitUntilAsync(
            () => fixture.ClientRoot.Child?.Other == "survivor",
            message: "the sibling in the same notification should still be applied");

        Assert.Equal("undated", fixture.ClientRoot.Child!.Value);

        // An undated timestamp carries no ticks, which the write path reads as no timestamp given and
        // stamps with the time it applied the value. The plain conversion turns the same wire value into
        // a real year-1 instant wherever the host is behind UTC, which would be stamped as it stands, so
        // this is what keeps a reverted call site from passing outside the zone the conversion throws in.
        var stampedTimestamp = fixture.ClientValueProperty.TryGetWriteTimestamp();
        Assert.NotNull(stampedTimestamp);
        Assert.True(
            stampedTimestamp >= beforeTheNotification,
            $"The applied value should be stamped with the time it landed, but it is {stampedTimestamp:O}.");
    }

    [Fact]
    public async Task WhenAPolledValueIsUncertain_ThenItIsApplied()
    {
        // Arrange
        await using var fixture = await InboundStatusFixture.StartAsync(_output);
        await fixture.WaitForPolledPropertiesAsync();

        // Act: the double needs no conversion, so only the status handling of the polling path is under test.
        OpcUaNodeStatusDriver.Publish(
            fixture.ServerService, fixture.DoubleProperty, 42.5d, StatusCodes.UncertainLastUsableValue);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => fixture.ClientRoot.Child?.DoubleValue == 42.5d,
            message: "the polled Uncertain value should reach the client");
    }

    [Fact]
    public async Task WhenAPolledPropertyNeedsConversion_ThenItIsApplied()
    {
        // Arrange
        await using var fixture = await InboundStatusFixture.StartAsync(_output);
        await fixture.WaitForPolledPropertiesAsync();

        // Act: the node is a Double on the wire, so only a converting path can land it in a decimal property.
        OpcUaNodeStatusDriver.Publish(
            fixture.ServerService, fixture.DecimalProperty, 12.5d, StatusCodes.Good);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => fixture.ClientRoot.Child?.DecimalValue == 12.5m,
            message: "the polled value should be converted to the property's type");
    }

    [Fact]
    public async Task WhenAPolledValueCannotBeConverted_ThenTheFailureIsNotLoggedOnEveryPoll()
    {
        // Arrange: the change-detection cache is deliberately left behind when a conversion fails, so a
        // converter that starts working still recovers the value. Every later poll therefore retries the
        // same failing conversion, and at the default one second interval an unguarded log never stops.
        var conversionFailures = new CountingLoggerProvider("Failed to convert a polled value");
        await using var fixture = await InboundStatusFixture.StartAsync(
            _output,
            valueConverter: new ThrowOnSentinelConverter(42.5d),
            extraClientLoggerProvider: conversionFailures);
        await fixture.WaitForPolledPropertiesAsync();

        // Act
        OpcUaNodeStatusDriver.Publish(
            fixture.ServerService, fixture.DoubleProperty, 42.5d, StatusCodes.Good);

        await AsyncTestHelpers.WaitUntilAsync(
            () => conversionFailures.Count >= 1,
            message: "the failing conversion should be reported once");

        // Assert: the polled read count is the clock, so this spans real poll cycles without a delay.
        var readsWhenFirstReported = fixture.PolledReadCount;
        await AsyncTestHelpers.WaitUntilAsync(
            () => fixture.PolledReadCount >= readsWhenFirstReported + 20,
            message: "further polls should have retried the same failing conversion");

        Assert.Equal(1, conversionFailures.Count);
    }

    [Fact]
    public async Task WhenOnePropertysApplyThrowsDuringInitialLoad_ThenTheOtherValuesStillApplyAndTheFailureStaysVisible()
    {
        // Arrange & Act: starting the client runs the initial state load, and the interceptor rejects
        // the value the Other node holds on the server, so applying the loaded snapshot throws for
        // that one property on every connect attempt. The stable-connection waits are skipped because
        // each of those attempts is torn down within milliseconds.
        await using var fixture = await InboundStatusFixture.StartAsync(
            _output,
            clientInterceptor: new ThrowOnValueInterceptor(InboundStatusFixture.OtherInitialValue),
            waitForClientConnection: false);

        // Assert: the sibling arriving proves the rejected value did not keep the rest of the
        // server's state out of the model.
        await fixture.WaitForClientValueAsync("initial");
        Assert.NotEqual(InboundStatusFixture.OtherInitialValue, fixture.ClientRoot.Child!.Other);

        // The model refused part of the snapshot, so the load must keep failing visibly rather than
        // certify a state it only partially holds.
        await AsyncTestHelpers.WaitUntilAsync(
            () => fixture.ClientSource.Diagnostics.LastError is not null,
            message: "the rejected value should be recorded on the source diagnostics");
        Assert.NotEqual(SourceState.Synchronized, fixture.ClientSource.State);
    }
}
