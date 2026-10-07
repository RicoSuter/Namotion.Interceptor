using Namotion.Devices.Shelly.Model;
using Xunit;

namespace Namotion.Devices.Shelly.Tests;

public class ShellyEnergyMeterUpdateTests
{
    [Fact]
    public void WhenUpdateFromStatus_ThenPhaseValuesMappedCorrectly()
    {
        // Arrange
        var meter = new ShellyEnergyMeter();
        var status = new ShellyEmStatus
        {
            PhaseAVoltage = 230.1m,
            PhaseACurrent = 1.5m,
            PhaseAFrequency = 50.0m,
            PhaseAActivePower = 345.0m,
            PhaseAApparentPower = 350.0m,
            PhaseAPowerFactor = 0.98m,
            PhaseBVoltage = 229.5m,
            PhaseBCurrent = 2.1m,
            PhaseBFrequency = 50.0m,
            PhaseBActivePower = 482.0m,
            PhaseBApparentPower = 490.0m,
            PhaseBPowerFactor = 0.97m,
            PhaseCVoltage = 231.0m,
            PhaseCCurrent = 0.8m,
            PhaseCFrequency = 50.0m,
            PhaseCActivePower = 184.0m,
            PhaseCApparentPower = 188.0m,
            PhaseCPowerFactor = 0.96m,
            TotalActivePower = 1011.0m,
            TotalApparentPower = 1028.0m,
            TotalCurrent = 4.4m,
            NeutralCurrent = 0.3m
        };

        // Act
        meter.UpdateFromStatus(status);

        // Assert
        Assert.Equal(230.1m, meter.Phases[0].ElectricalVoltage);
        Assert.Equal(1.5m, meter.Phases[0].ElectricalCurrent);
        Assert.Equal(50.0m, meter.Phases[0].ElectricalFrequency);
        Assert.Equal(345.0m, meter.Phases[0].ActivePower);
        Assert.Equal(0.98m, meter.Phases[0].PowerFactor);

        Assert.Equal(229.5m, meter.Phases[1].ElectricalVoltage);
        Assert.Equal(2.1m, meter.Phases[1].ElectricalCurrent);
        Assert.Equal(482.0m, meter.Phases[1].ActivePower);

        Assert.Equal(231.0m, meter.Phases[2].ElectricalVoltage);
        Assert.Equal(0.8m, meter.Phases[2].ElectricalCurrent);
        Assert.Equal(184.0m, meter.Phases[2].ActivePower);
    }

    [Fact]
    public void WhenUpdateFromStatus_ThenMeterValuesMapped()
    {
        // Arrange
        var meter = new ShellyEnergyMeter();
        var status = new ShellyEmStatus
        {
            TotalActivePower = 1011.0m,
            TotalApparentPower = 1028.0m,
            TotalCurrent = 4.4m,
            NeutralCurrent = 0.3m
        };

        // Act
        meter.UpdateFromStatus(status);

        // Assert
        Assert.Equal(1011.0m, meter.MeasuredPower);
        Assert.Equal(1028.0m, meter.ApparentPower);
        Assert.Equal(4.4m, meter.ElectricalCurrent);
        Assert.Equal(0.3m, meter.NeutralCurrent);
    }

    [Fact]
    public void WhenUpdateFromDataStatus_ThenDeviceCountersMappedAndNettedCountersUntouched()
    {
        // Arrange
        var meter = new ShellyEnergyMeter();
        var dataStatus = new ShellyEmDataStatus
        {
            TotalActiveEnergy = 15000.5m,
            TotalActiveReturnedEnergy = 2000.3m,
            PhaseATotalActiveEnergy = 5000.1m,
            PhaseATotalReturnedEnergy = 700.0m,
            PhaseBTotalActiveEnergy = 6000.2m,
            PhaseBTotalReturnedEnergy = 800.0m,
            PhaseCTotalActiveEnergy = 4000.2m,
            PhaseCTotalReturnedEnergy = 500.3m
        };

        // Act
        meter.UpdateFromDataStatus(dataStatus);

        // Assert
        Assert.Equal(15000.5m, meter.TotalImportedPhaseEnergy);
        Assert.Equal(2000.3m, meter.TotalExportedPhaseEnergy);
        Assert.Equal(5000.1m, meter.Phases[0].TotalImportedEnergy);
        Assert.Equal(700.0m, meter.Phases[0].TotalExportedEnergy);
        Assert.Equal(6000.2m, meter.Phases[1].TotalImportedEnergy);
        Assert.Equal(800.0m, meter.Phases[1].TotalExportedEnergy);
        Assert.Equal(4000.2m, meter.Phases[2].TotalImportedEnergy);
        Assert.Equal(500.3m, meter.Phases[2].TotalExportedEnergy);
        Assert.Null(meter.IsTotalEnergyPhaseNetted);
        Assert.Null(meter.TotalImportedEnergy);
        Assert.Null(meter.TotalExportedEnergy);
    }

    [Fact]
    public void WhenUpdateFromDataStatusWithoutPhaseNetting_ThenCountersFollowPhaseSums()
    {
        // Arrange
        var meter = new ShellyEnergyMeter();
        meter.UsePhaseEnergy();

        // Act
        meter.UpdateFromDataStatus(new ShellyEmDataStatus { TotalActiveEnergy = 15000.5m, TotalActiveReturnedEnergy = 2000.3m });

        // Assert
        Assert.False(meter.IsTotalEnergyPhaseNetted);
        Assert.Equal(15000.5m, meter.TotalImportedEnergy);
        Assert.Equal(2000.3m, meter.TotalExportedEnergy);
    }

    [Fact]
    public void WhenUpdateFromDataStatusWhilePhaseNetted_ThenNettedCountersAreKept()
    {
        // Arrange
        var meter = new ShellyEnergyMeter
        {
            IsTotalEnergyPhaseNetted = true,
            TotalImportedEnergy = 14000.0m,
            TotalExportedEnergy = 1000.0m
        };

        // Act
        meter.UpdateFromDataStatus(new ShellyEmDataStatus { TotalActiveEnergy = 15000.5m, TotalActiveReturnedEnergy = 2000.3m });

        // Assert
        Assert.Equal(14000.0m, meter.TotalImportedEnergy);
        Assert.Equal(1000.0m, meter.TotalExportedEnergy);
        Assert.Equal(15000.5m, meter.TotalImportedPhaseEnergy);
    }

    [Fact]
    public void WhenCreated_ThenHasThreePhases()
    {
        // Act
        var meter = new ShellyEnergyMeter();

        // Assert
        Assert.Equal(3, meter.Phases.Length);
        Assert.Equal("Phase A", meter.Phases[0].Title);
        Assert.Equal("Phase B", meter.Phases[1].Title);
        Assert.Equal("Phase C", meter.Phases[2].Title);
    }
}
