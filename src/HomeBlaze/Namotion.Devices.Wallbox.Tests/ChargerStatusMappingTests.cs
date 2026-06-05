using Namotion.Devices.Wallbox.Model;
using Xunit;

namespace Namotion.Devices.Wallbox.Tests;

public class ChargerStatusMappingTests
{
    [Theory]
    [InlineData(0, WallboxChargerStatus.Disconnected)]
    [InlineData(14, WallboxChargerStatus.Error)]
    [InlineData(15, WallboxChargerStatus.Error)]
    [InlineData(161, WallboxChargerStatus.Ready)]
    [InlineData(162, WallboxChargerStatus.Ready)]
    [InlineData(163, WallboxChargerStatus.Disconnected)]
    [InlineData(164, WallboxChargerStatus.Waiting)]
    [InlineData(165, WallboxChargerStatus.Locked)]
    [InlineData(166, WallboxChargerStatus.Updating)]
    [InlineData(177, WallboxChargerStatus.Scheduled)]
    [InlineData(179, WallboxChargerStatus.Scheduled)]
    [InlineData(178, WallboxChargerStatus.Paused)]
    [InlineData(182, WallboxChargerStatus.Paused)]
    [InlineData(180, WallboxChargerStatus.WaitingForCarDemand)]
    [InlineData(181, WallboxChargerStatus.WaitingForCarDemand)]
    [InlineData(183, WallboxChargerStatus.WaitingInQueueByPowerSharing)]
    [InlineData(184, WallboxChargerStatus.WaitingInQueueByPowerSharing)]
    [InlineData(185, WallboxChargerStatus.WaitingInQueueByPowerBoost)]
    [InlineData(186, WallboxChargerStatus.WaitingInQueueByPowerBoost)]
    [InlineData(187, WallboxChargerStatus.WaitingMidFailed)]
    [InlineData(188, WallboxChargerStatus.WaitingMidSafetyMarginExceeded)]
    [InlineData(189, WallboxChargerStatus.WaitingInQueueByEcoSmart)]
    [InlineData(193, WallboxChargerStatus.Charging)]
    [InlineData(194, WallboxChargerStatus.Charging)]
    [InlineData(195, WallboxChargerStatus.Charging)]
    [InlineData(196, WallboxChargerStatus.Discharging)]
    [InlineData(209, WallboxChargerStatus.Locked)]
    [InlineData(210, WallboxChargerStatus.LockedCarConnected)]
    public void WhenStatusIdMapped_ThenReturnsExpectedStatus(int statusId, WallboxChargerStatus expected)
    {
        // Arrange
        var response = new ChargerStatusResponse { StatusId = statusId };

        // Act
        var result = response.Status;

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(99)]
    [InlineData(999)]
    public void WhenStatusIdUnknown_ThenReturnsUnknown(int statusId)
    {
        // Arrange
        var response = new ChargerStatusResponse { StatusId = statusId };

        // Act
        var result = response.Status;

        // Assert
        Assert.Equal(WallboxChargerStatus.Unknown, result);
    }
}
