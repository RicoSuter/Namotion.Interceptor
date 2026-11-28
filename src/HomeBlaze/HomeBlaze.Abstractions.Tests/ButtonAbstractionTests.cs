using HomeBlaze.Abstractions.Inputs;

namespace HomeBlaze.Abstractions.Tests;

public class ButtonAbstractionTests
{
    [Fact]
    public void WhenButtonEventSetsNoPressCount_ThenItIsOne()
    {
        // Arrange & Act
        var buttonEvent = new ButtonEvent
        {
            Button = new TestButton(),
            ButtonState = ButtonState.Release,
            Timestamp = DateTimeOffset.UnixEpoch,
            DeviceId = "button"
        };

        // Assert
        Assert.Equal(1, buttonEvent.PressCount);
    }

    private sealed class TestButton : IMultiPressButtonDevice
    {
        public ButtonState? ButtonState => null;

        public int? PressCount => null;
    }
}
