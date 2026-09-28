namespace Namotion.Devices.Luxtronik.Tests;

public class LuxtronikModelTests
{
    [Fact]
    public void WhenCompressorFlagIsSet_ThenCompressorIsRunning()
    {
        // Arrange
        var status = new LuxtronikOperatingStatus();

        // Act
        status.HeatPumpStatus = LuxtronikHeatPumpStatus.Compressor2;

        // Assert
        Assert.True(status.IsCompressorRunning);
        Assert.False(status.IsAuxiliaryHeaterRunning);
    }

    [Fact]
    public void WhenHeatPumpStatusIsUnknown_ThenRunningFlagsAreNull()
    {
        // Act
        var status = new LuxtronikOperatingStatus();

        // Assert
        Assert.Null(status.IsCompressorRunning);
        Assert.Null(status.IsAuxiliaryHeaterRunning);
    }

    [Theory]
    [InlineData(true, false, LuxtronikSmartGridState.Locked)]
    [InlineData(false, false, LuxtronikSmartGridState.Reduced)]
    [InlineData(false, true, LuxtronikSmartGridState.Normal)]
    [InlineData(true, true, LuxtronikSmartGridState.Increased)]
    public void WhenEvuSignalsAreSet_ThenSmartGridStateFollowsTheManual(bool evu1, bool evu2, LuxtronikSmartGridState expected)
    {
        // Arrange
        var smartGrid = new LuxtronikSmartGrid();

        // Act
        smartGrid.Evu1 = evu1;
        smartGrid.Evu2 = evu2;

        // Assert
        Assert.Equal(expected, smartGrid.State);
    }

    [Fact]
    public void WhenTemperaturesAreConstructed_ThenSensorsUseTheirRegisterAddresses()
    {
        // Act
        var temperatures = new LuxtronikTemperatures();

        // Assert
        Assert.Equal(10108, temperatures.Outside.BaseAddress);
        Assert.Equal(10120, temperatures.HotWater.BaseAddress);
        Assert.Null(((ILuxtronikGatedSubject)temperatures.Outside).MinimumFirmwareVersion);
        Assert.Equal(new Version(3, 92, 0), ((ILuxtronikGatedSubject)temperatures.HeatSourceInlet).MinimumFirmwareVersion);
        Assert.Equal("Outside", temperatures.Outside.Title);
    }
}
