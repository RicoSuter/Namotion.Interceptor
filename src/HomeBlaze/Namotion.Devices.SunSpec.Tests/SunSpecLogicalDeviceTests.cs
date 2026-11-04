using HomeBlaze.Abstractions.Common;
using HomeBlaze.Abstractions.Devices;
using Namotion.Devices.SunSpec.Models;
using Namotion.Devices.SunSpec.Tests.Testing;

namespace Namotion.Devices.SunSpec.Tests;

public class SunSpecLogicalDeviceTests
{
    [Fact]
    public void WhenCommonModelIsSet_ThenDeviceInfoAndTitleComeFromIt()
    {
        // Arrange
        var device = TestRoot.Attach(new SunSpecLogicalDevice(1, 0));
        var common = new SunSpecCommon(40002, 65);

        // Act
        device.Common = common;
        common.Mn = "SolarEdge";
        common.Md = "SE-NX20K";
        common.SN = "7E1A2B3C";
        common.Vr = "4.20.1";

        // Assert
        IDeviceInfo deviceInfo = device;
        Assert.Equal("SolarEdge", deviceInfo.Manufacturer);
        Assert.Equal("SE-NX20K", deviceInfo.Model);
        Assert.Equal("7E1A2B3C", deviceInfo.SerialNumber);
        Assert.Equal("4.20.1", ((ISoftwareState)device).SoftwareVersion);
        Assert.Equal("SolarEdge SE-NX20K", device.Title);
    }

    [Fact]
    public void WhenThereIsNoCommonModel_ThenTitleNamesUnitAndPosition()
    {
        // Act
        var device = TestRoot.Attach(new SunSpecLogicalDevice(3, 1));

        // Assert
        Assert.Equal("Unit 3, device 2", device.Title);
    }

    [Fact]
    public void WhenListingModels_ThenCommonComesFirst()
    {
        // Arrange
        var device = TestRoot.Attach(new SunSpecLogicalDevice(1, 0));
        var common = new SunSpecCommon(40002, 65);
        var inverter = new SunSpecInverter(103, 40069, 50);

        // Act
        device.Common = common;
        device.Models = [inverter];

        // Assert
        Assert.Equal<ISunSpecModel>([common, inverter], device.GetModels());
    }
}
