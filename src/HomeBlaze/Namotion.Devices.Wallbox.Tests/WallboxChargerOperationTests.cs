using System.Reflection;
using HomeBlaze.Abstractions.Attributes;
using Xunit;

namespace Namotion.Devices.Wallbox.Tests;

public class WallboxChargerOperationTests
{
    [Theory]
    [InlineData(nameof(WallboxCharger.UnlockAsync))]
    [InlineData(nameof(WallboxCharger.UnlockChargePortAsync))]
    [InlineData(nameof(WallboxCharger.ToggleChargePortLockAsync))]
    public void WhenAnOperationCanUnlock_ThenItRequiresConfirmation(string methodName)
    {
        // Act
        var attribute = typeof(WallboxCharger).GetMethod(methodName)!.GetCustomAttribute<OperationAttribute>();

        // Assert
        Assert.NotNull(attribute);
        Assert.True(attribute.RequiresConfirmation);
    }
}
