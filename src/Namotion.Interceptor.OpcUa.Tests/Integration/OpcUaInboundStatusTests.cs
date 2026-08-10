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

        // Act: both properties travel in one notification, so the sentinel arriving on the sibling proves
        // the notification was processed and the assertion below is not racing it.
        fixture.PublishPair("from-faulted-sensor", StatusCodes.BadDeviceFailure, "sentinel", StatusCodes.Good);

        await AsyncTestHelpers.WaitUntilAsync(
            () => fixture.ClientRoot.Child?.Other == "sentinel",
            message: "the notification carrying both properties should reach the client");

        // Assert
        Assert.Equal("initial", fixture.ClientRoot.Child!.Value);
    }

    [Fact]
    public async Task WhenOneValuesConversionThrows_ThenTheRestOfTheNotificationIsStillApplied()
    {
        // Arrange
        await using var fixture = await InboundStatusFixture.StartAsync(
            _output, valueConverter: new ThrowOnSentinelConverter("poison"));

        // Act: both properties travel in one notification, so the failing conversion of the first can
        // only be contained if it does not abort the processing of the notification as a whole.
        fixture.PublishPair("poison", StatusCodes.Good, "survivor", StatusCodes.Good);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => fixture.ClientRoot.Child?.Other == "survivor",
            message: "the sibling in the same notification should still be applied");

        Assert.Equal("initial", fixture.ClientRoot.Child!.Value);
    }
}
