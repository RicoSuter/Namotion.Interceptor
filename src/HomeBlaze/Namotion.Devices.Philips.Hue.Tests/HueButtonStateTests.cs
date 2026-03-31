using HueApi.Models;
using Xunit;
using ButtonState = HomeBlaze.Abstractions.Inputs.ButtonState;

namespace Namotion.Devices.Philips.Hue.Tests;

public class HueButtonStateTests
{
    [Theory]
    [InlineData(ButtonEvent.initial_press, ButtonState.Down)]
    [InlineData(ButtonEvent.repeat, ButtonState.Repeat)]
    [InlineData(ButtonEvent.short_release, ButtonState.Release)]
    [InlineData(ButtonEvent.long_release, ButtonState.LongRelease)]
    public void WhenEventType_ThenGetButtonStateReturnsExpected(ButtonEvent eventType, ButtonState expectedState)
    {
        // Act
        var result = HueButton.GetButtonState(eventType);

        // Assert
        Assert.Equal(expectedState, result);
    }

    [Fact]
    public void WhenInitialPress_ThenStateIsDown()
    {
        // Arrange
        var initialTimestamp = DateTimeOffset.UtcNow.AddSeconds(-10);
        var buttonResource = TestHelpers.CreateButtonResource(null);
        var buttonDevice = TestHelpers.CreateButtonDevice([buttonResource]);
        var button = buttonDevice.Buttons[0];

        // Act - update with initial_press event at a new timestamp
        var pressTimestamp = DateTimeOffset.UtcNow;
        var updatedResource = TestHelpers.CreateButtonResource(ButtonEvent.initial_press, pressTimestamp);
        updatedResource.Id = buttonResource.Id; // keep same ID for matching
        button.Update(updatedResource, false);

        // Assert
        Assert.Equal(ButtonState.Down, button.ButtonState);
    }

    [Fact]
    public void WhenRepeat_ThenStateIsRepeat()
    {
        // Arrange
        var buttonResource = TestHelpers.CreateButtonResource(null);
        var buttonDevice = TestHelpers.CreateButtonDevice([buttonResource]);
        var button = buttonDevice.Buttons[0];

        // First: set to Down via initial_press
        var pressTimestamp = DateTimeOffset.UtcNow;
        var pressResource = TestHelpers.CreateButtonResource(ButtonEvent.initial_press, pressTimestamp);
        pressResource.Id = buttonResource.Id;
        button.Update(pressResource, false);
        Assert.Equal(ButtonState.Down, button.ButtonState);

        // Act - update with repeat event at a new timestamp
        var repeatTimestamp = pressTimestamp.AddMilliseconds(500);
        var repeatResource = TestHelpers.CreateButtonResource(ButtonEvent.repeat, repeatTimestamp);
        repeatResource.Id = buttonResource.Id;
        button.Update(repeatResource, false);

        // Assert
        Assert.Equal(ButtonState.Repeat, button.ButtonState);
    }

    [Fact]
    public void WhenShortRelease_ThenResetsToNone()
    {
        // Arrange
        var buttonResource = TestHelpers.CreateButtonResource(null);
        var buttonDevice = TestHelpers.CreateButtonDevice([buttonResource]);
        var button = buttonDevice.Buttons[0];

        // First: set to Down via initial_press
        var pressTimestamp = DateTimeOffset.UtcNow;
        var pressResource = TestHelpers.CreateButtonResource(ButtonEvent.initial_press, pressTimestamp);
        pressResource.Id = buttonResource.Id;
        button.Update(pressResource, false);

        // Act - update with short_release
        var releaseTimestamp = pressTimestamp.AddMilliseconds(200);
        var releaseResource = TestHelpers.CreateButtonResource(ButtonEvent.short_release, releaseTimestamp);
        releaseResource.Id = buttonResource.Id;
        button.Update(releaseResource, false);

        // Assert - Release resets to None after processing
        Assert.Equal(ButtonState.None, button.ButtonState);
    }

    [Fact]
    public void WhenLongRelease_ThenResetsToNone()
    {
        // Arrange
        var buttonResource = TestHelpers.CreateButtonResource(null);
        var buttonDevice = TestHelpers.CreateButtonDevice([buttonResource]);
        var button = buttonDevice.Buttons[0];

        // First: set to Down via initial_press
        var pressTimestamp = DateTimeOffset.UtcNow;
        var pressResource = TestHelpers.CreateButtonResource(ButtonEvent.initial_press, pressTimestamp);
        pressResource.Id = buttonResource.Id;
        button.Update(pressResource, false);

        // Act - update with long_release
        var releaseTimestamp = pressTimestamp.AddSeconds(2);
        var releaseResource = TestHelpers.CreateButtonResource(ButtonEvent.long_release, releaseTimestamp);
        releaseResource.Id = buttonResource.Id;
        button.Update(releaseResource, false);

        // Assert - LongRelease resets to None after processing
        Assert.Equal(ButtonState.None, button.ButtonState);
    }

    [Fact]
    public void WhenRepeat_ThenDoesNotResetToNone()
    {
        // Arrange
        var buttonResource = TestHelpers.CreateButtonResource(null);
        var buttonDevice = TestHelpers.CreateButtonDevice([buttonResource]);
        var button = buttonDevice.Buttons[0];

        // First: set to Down via initial_press
        var pressTimestamp = DateTimeOffset.UtcNow;
        var pressResource = TestHelpers.CreateButtonResource(ButtonEvent.initial_press, pressTimestamp);
        pressResource.Id = buttonResource.Id;
        button.Update(pressResource, false);

        // Act - update with repeat
        var repeatTimestamp = pressTimestamp.AddMilliseconds(500);
        var repeatResource = TestHelpers.CreateButtonResource(ButtonEvent.repeat, repeatTimestamp);
        repeatResource.Id = buttonResource.Id;
        button.Update(repeatResource, false);

        // Assert - Repeat stays as Repeat (does NOT reset to None)
        Assert.Equal(ButtonState.Repeat, button.ButtonState);
    }

    [Fact]
    public void WhenDuplicateRepeat_ThenStateNotChanged()
    {
        // Arrange
        var buttonResource = TestHelpers.CreateButtonResource(null);
        var buttonDevice = TestHelpers.CreateButtonDevice([buttonResource]);
        var button = buttonDevice.Buttons[0];

        // First: set to Down, then Repeat
        var pressTimestamp = DateTimeOffset.UtcNow;
        var pressResource = TestHelpers.CreateButtonResource(ButtonEvent.initial_press, pressTimestamp);
        pressResource.Id = buttonResource.Id;
        button.Update(pressResource, false);

        var firstRepeatTimestamp = pressTimestamp.AddMilliseconds(500);
        var firstRepeatResource = TestHelpers.CreateButtonResource(ButtonEvent.repeat, firstRepeatTimestamp);
        firstRepeatResource.Id = buttonResource.Id;
        button.Update(firstRepeatResource, false);
        Assert.Equal(ButtonState.Repeat, button.ButtonState);

        // Act - send another repeat with a new timestamp
        // The duplicate repeat check: if newButtonState == Repeat AND ButtonState == Repeat, skip
        var secondRepeatTimestamp = firstRepeatTimestamp.AddMilliseconds(500);
        var secondRepeatResource = TestHelpers.CreateButtonResource(ButtonEvent.repeat, secondRepeatTimestamp);
        secondRepeatResource.Id = buttonResource.Id;
        button.Update(secondRepeatResource, false);

        // Assert - still Repeat but the duplicate was suppressed (no new event fired)
        // The state remains Repeat because the duplicate check prevents re-setting
        Assert.Equal(ButtonState.Repeat, button.ButtonState);
    }

    [Fact]
    public void WhenInitialization_ThenButtonStateRemainsNone()
    {
        // Arrange - create with initialization = true (via the constructor path in HueButtonDevice)
        var buttonResource = TestHelpers.CreateButtonResource(ButtonEvent.initial_press, DateTimeOffset.UtcNow);
        var buttonDevice = TestHelpers.CreateButtonDevice([buttonResource]);

        // The HueButtonDevice constructor calls Update with Buttons.Length == 0 for first time,
        // which means initialization = true, so RefreshButtonState is NOT called
        var button = buttonDevice.Buttons[0];

        // Assert - during initialization, button state should be None (no event processing)
        Assert.Equal(ButtonState.None, button.ButtonState);
    }

    [Fact]
    public void WhenNoButtonReport_ThenInternalButtonStateIsNone()
    {
        // Arrange
        var buttonResource = TestHelpers.CreateButtonResource(null);
        var buttonDevice = TestHelpers.CreateButtonDevice([buttonResource]);
        var button = buttonDevice.Buttons[0];

        // Assert
        Assert.Equal(ButtonState.None, button.ButtonState);
    }
}
