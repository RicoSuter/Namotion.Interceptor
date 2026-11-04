using Xunit;

namespace Namotion.Devices.Wallbox.Tests;

public class WallboxChargerEnergyTests
{
    [Fact]
    public void WhenSessionsWereNotRead_ThenTotalConsumedEnergyIsNull()
    {
        // Act
        var value = WallboxCharger.GetTotalConsumedEnergy(currentValue: null, sessionsEnergy: null, currentSessionEnergy: 5000m);

        // Assert
        Assert.Null(value);
    }

    [Fact]
    public void WhenCharging_ThenCurrentSessionIsAdded()
    {
        // Act
        var value = WallboxCharger.GetTotalConsumedEnergy(currentValue: 100000m, sessionsEnergy: 100000m, currentSessionEnergy: 5000m);

        // Assert
        Assert.Equal(105000m, value);
    }

    [Fact]
    public void WhenUnpluggedBeforeSessionsRefresh_ThenValueDoesNotDecrease()
    {
        // Act
        var value = WallboxCharger.GetTotalConsumedEnergy(currentValue: 105000m, sessionsEnergy: 100000m, currentSessionEnergy: 0m);

        // Assert
        Assert.Equal(105000m, value);
    }

    [Fact]
    public void WhenSessionsRefreshIncludesFinishedSession_ThenValueContinues()
    {
        // Act
        var value = WallboxCharger.GetTotalConsumedEnergy(currentValue: 105000m, sessionsEnergy: 105200m, currentSessionEnergy: 0m);

        // Assert
        Assert.Equal(105200m, value);
    }
}
