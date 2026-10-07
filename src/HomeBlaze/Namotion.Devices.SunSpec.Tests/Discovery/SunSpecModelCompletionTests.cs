using Namotion.Devices.SunSpec.Definitions;
using Namotion.Devices.SunSpec.Discovery;
using Namotion.Devices.SunSpec.Models;
using Namotion.Devices.SunSpec.Tests.Models;
using Namotion.Devices.SunSpec.Tests.Testing;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;

namespace Namotion.Devices.SunSpec.Tests.Discovery;

public class SunSpecModelCompletionTests
{
    private const int ModelAddress = 40002;

    [Fact]
    public void WhenTheModelHasItsFullLength_ThenNoPropertyIsUnavailable()
    {
        // Arrange
        var model = TestRoot.Attach(new SunSpecBasicSettings(ModelAddress, 30));

        // Act
        var unavailable = SunSpecDiscovery.GetUnavailableProperties(model);

        // Assert
        Assert.Empty(unavailable);
    }

    [Fact]
    public void WhenAScaleFactorIsBeyondTheReportedLength_ThenThePropertiesItScalesAreUnavailableToo()
    {
        // Arrange: model 121 is 30 registers long; at 29 its last point, ECPNomHz_SF, is missing.
        var model = TestRoot.Attach(new SunSpecBasicSettings(ModelAddress, 29));

        // Act
        var unavailable = SunSpecDiscovery.GetUnavailableProperties(model);

        // Assert
        Assert.Equal(2, unavailable.Count);
        Assert.Contains(new PropertyReference(model, nameof(SunSpecBasicSettings.ECPNomHz_SF)), unavailable);
        Assert.Contains(new PropertyReference(model, nameof(SunSpecBasicSettings.ECPNomHz)), unavailable);
    }

    [Fact]
    public void WhenAGroupPointIsScaledByAnUnavailableScaleFactor_ThenItIsUnavailable()
    {
        // Arrange: registers ID, L, W, W_SF, N, A_SF, channel[0].A, channel[1].A; the model reports a length that ends before A_SF.
        var definition = SunSpecDynamicModelTests.ParseDefinition();
        var model = new SunSpecDynamicModel(definition, ModelAddress, 3);
        ((ISunSpecGroupOwner)model).UpdateGroups(SunSpecLayout.Resolve(definition, ModelAddress, [64999, 6, 1500, 0, 2, 0xFFFF, 10, 20]));
        TestRoot.Attach(model);
        model.EnsureProperties();
        var channels = (SunSpecDynamicGroup[])model.TryGetRegisteredSubject()!.TryGetProperty("Channel")!.GetValue()!;

        // Act
        var unavailable = SunSpecDiscovery.GetUnavailableProperties(model);

        // Assert
        Assert.Equal(3, unavailable.Count);
        Assert.Contains(new PropertyReference(model, "A_SF"), unavailable);
        Assert.All(channels, channel => Assert.Contains(new PropertyReference(channel, "A"), unavailable));
    }

    [Fact]
    public void WhenTheModelIdRegisterHoldsAPreviousPoll_ThenTheDiscoveredModelIdReplacesIt()
    {
        // Arrange
        var model = TestRoot.Attach(new SunSpecBasicSettings(ModelAddress, 30));
        model.ModelIdRegister = 122;

        // Act
        SunSpecDiscovery.ApplyDiscoveredModelId(model, new object());

        // Assert
        Assert.Equal((ushort)121, model.ModelIdRegister);
    }

    [Theory]
    [InlineData(new[] { 1 }, new[] { 1 }, null)]
    [InlineData(new[] { 1 }, new int[0], "No SunSpec unit found")]
    [InlineData(new[] { 1, 2 }, new[] { 1 }, "Unit 2 not found")]
    [InlineData(new[] { 1, 2, 3 }, new[] { 1 }, "Units 2, 3 not found")]
    public void WhenUnitsAreDiscovered_ThenTheStatusMessageNamesTheMissingOnes(int[] configured, int[] found, string? expected)
    {
        // Arrange
        var unitIds = configured.Select(unitId => (byte)unitId).ToArray();
        var units = found.ToDictionary(unitId => unitId, unitId => new SunSpecUnit((byte)unitId));

        // Act
        var message = SunSpecDiscovery.GetStatusMessage(unitIds, units);

        // Assert
        Assert.Equal(expected, message);
    }
}
