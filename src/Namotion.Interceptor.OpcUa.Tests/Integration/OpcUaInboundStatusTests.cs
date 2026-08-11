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

        // Act
        fixture.PublishPair("undated", StatusCodes.Good, "survivor", StatusCodes.Good);

        // Assert: the pair travels in one notification, so the sibling's arrival is what proves the
        // undated value did not take the whole notification down with it.
        await AsyncTestHelpers.WaitUntilAsync(
            () => fixture.ClientRoot.Child?.Other == "survivor",
            message: "the sibling in the same notification should still be applied");

        Assert.Equal("undated", fixture.ClientRoot.Child!.Value);
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
    public async Task WhenOnePropertysApplyThrowsDuringInitialLoad_ThenTheSourceStillReachesSynchronized()
    {
        // Arrange: the interceptor rejects the value both string nodes hold on the server, so applying
        // the loaded snapshot throws. The connected-wait is relaxed because that value can never land,
        // and the fixture would otherwise time out before this test's own assertion runs.
        await using var fixture = await InboundStatusFixture.StartAsync(
            _output,
            clientInterceptor: new ThrowOnValueInterceptor("initial"),
            waitForInitialValue: false);

        // Act & Assert: a rejected value must not abort the load, which would retry the connect forever.
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(() => fixture.ClientSource.State == SourceState.Synchronized);
        }
        catch (TimeoutException)
        {
            // Read here rather than passed to WaitUntilAsync, whose message is built before the wait.
            Assert.Fail($"The source should reach Synchronized, but it is {fixture.ClientSource.State}.");
        }
    }
}
