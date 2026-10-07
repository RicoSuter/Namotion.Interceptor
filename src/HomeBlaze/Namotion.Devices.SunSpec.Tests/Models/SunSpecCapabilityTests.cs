using HomeBlaze.Abstractions.Devices.Energy;
using HomeBlaze.Abstractions.Sensors;
using Namotion.Devices.SunSpec.Models;
using Namotion.Devices.SunSpec.Tests.Testing;

namespace Namotion.Devices.SunSpec.Tests.Models;

public class SunSpecCapabilityTests
{
    [Fact]
    public void WhenInverterProduces_ThenPowerMeterReportsExport()
    {
        // Arrange
        var inverter = TestRoot.Attach(new SunSpecInverter(103, 40069, 50));

        // Act
        inverter.W = 1500m;
        inverter.WH = 123456m;
        inverter.Hz = 50.01m;
        inverter.TmpCab = 45.2m;

        // Assert
        IPowerMeter powerMeter = inverter;
        Assert.Equal(-1500m, powerMeter.MeasuredPower);
        Assert.Equal(123456m, powerMeter.TotalExportedEnergy);
        Assert.Null(powerMeter.TotalImportedEnergy);
        Assert.Equal(50.01m, ((IElectricalFrequencySensor)inverter).ElectricalFrequency);
        Assert.Equal(45.2m, ((ITemperatureSensor)inverter).Temperature);
    }

    [Fact]
    public void WhenFloatInverterProduces_ThenPowerMeterReportsExport()
    {
        // Arrange
        var inverter = TestRoot.Attach(new SunSpecFloatInverter(113, 40069, 60));

        // Act
        inverter.W = 2500m;

        // Assert
        Assert.Equal(-2500m, ((IPowerMeter)inverter).MeasuredPower);
    }

    [Fact]
    public void WhenMeterMeasures_ThenPowerMeterReportsItsValues()
    {
        // Arrange
        var meter = TestRoot.Attach(new SunSpecAcMeter(203, 40188, 105));

        // Act
        meter.W = -800m;
        meter.TotWhImp = 1000m;
        meter.TotWhExp = 2000m;
        meter.Hz = 50m;

        // Assert
        IPowerMeter powerMeter = meter;
        Assert.Equal(-800m, powerMeter.MeasuredPower);
        Assert.Equal(1000m, powerMeter.TotalImportedEnergy);
        Assert.Equal(2000m, powerMeter.TotalExportedEnergy);
        Assert.Equal(50m, ((IElectricalFrequencySensor)meter).ElectricalFrequency);
    }

    [Fact]
    public void WhenStorageReportsStateOfCharge_ThenBatteryLevelIsTheFraction()
    {
        // Arrange
        var storage = TestRoot.Attach(new SunSpecStorageCapacity(41090, 7));

        // Act
        storage.SoC = 0.455m;

        // Assert
        Assert.Equal(0.455m, ((IBatteryState)storage).BatteryLevel);
    }
}
