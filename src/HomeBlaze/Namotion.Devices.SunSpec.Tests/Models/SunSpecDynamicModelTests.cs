using System.Text;
using Namotion.Devices.SunSpec.Definitions;
using Namotion.Devices.SunSpec.Models;
using Namotion.Devices.SunSpec.Tests.Testing;
using Namotion.Interceptor;
using Namotion.Interceptor.Modbus.Attributes;
using Namotion.Interceptor.Registry;

namespace Namotion.Devices.SunSpec.Tests.Models;

public class SunSpecDynamicModelTests
{
    internal const string DefinitionJson = """
        { "id": 64999, "group": { "name": "vendor", "label": "Vendor Block", "points": [
            { "name": "ID", "type": "uint16", "size": 1 }, { "name": "L", "type": "uint16", "size": 1 },
            { "name": "W", "type": "int16", "size": 1, "units": "W", "sf": "W_SF", "label": "Power" },
            { "name": "W_SF", "type": "sunssf", "size": 1 }, { "name": "N", "type": "count", "size": 1 },
            { "name": "A_SF", "type": "sunssf", "size": 1 } ],
          "groups": [ { "name": "channel", "count": "N", "points": [
            { "name": "A", "type": "uint16", "size": 1, "units": "A", "sf": "A_SF" } ] } ] } }
        """;

    internal static SunSpecModelDefinition ParseDefinition() => SunSpecDefinitions.Parse(new MemoryStream(Encoding.UTF8.GetBytes(DefinitionJson)));

    private static SunSpecDynamicModel CreateAttachedModel()
    {
        var definition = ParseDefinition();
        var model = new SunSpecDynamicModel(definition, 40100, 6);
        ushort[] registers = [64999, 6, 1500, 0, 2, 0xFFFF, 10, 20];
        ((ISunSpecGroupOwner)model).UpdateGroups(SunSpecLayout.Resolve(definition, 40100, registers));
        TestRoot.Attach(model);
        model.EnsureProperties();
        return model;
    }

    [Fact]
    public void WhenAttached_ThenPointsBecomeRegisterProperties()
    {
        // Act
        var model = CreateAttachedModel();

        // Assert
        var power = model.TryGetRegisteredSubject()!.TryGetProperty("W")!;
        var attribute = Assert.Single(power.ReflectionAttributes.OfType<ModbusRegisterAttribute>());
        Assert.Equal(2, attribute.Address);
        Assert.Equal("W_SF", attribute.ScaleFactorProperty);
        Assert.Equal(typeof(decimal?), power.Type);
    }

    [Fact]
    public void WhenAttached_ThenRepeatingGroupsBecomeChildSubjects()
    {
        // Act
        var model = CreateAttachedModel();

        // Assert
        var channels = Assert.IsType<SunSpecDynamicGroup[]>(model.TryGetRegisteredSubject()!.TryGetProperty("Channel")!.GetValue());
        Assert.Equal(new[] { 40106, 40107 }, channels.Select(channel => channel.BaseAddress));
        Assert.NotNull(channels[1].TryGetRegisteredSubject()!.TryGetProperty("A"));
    }

    [Fact]
    public void WhenGroupPointUsesAModelScaleFactor_ThenProviderReturnsTheModelProperty()
    {
        // Arrange
        var model = CreateAttachedModel();
        var channels = (SunSpecDynamicGroup[])model.TryGetRegisteredSubject()!.TryGetProperty("Channel")!.GetValue()!;

        // Act
        var scaleFactor = channels[0].TryGetScaleFactorProperty("A");

        // Assert
        Assert.Equal(new PropertyReference(model, "A_SF"), scaleFactor);
    }

    [Fact]
    public void WhenModelIsUnknown_ThenOnlyItsPositionIsKnown()
    {
        // Act
        var model = new SunSpecUnknownModel(64998, 40200, 10);

        // Assert
        Assert.Equal(64998, model.ModelId);
        Assert.Equal(40200, model.BaseAddress);
        Assert.Equal("Unknown model 64998", model.Title);
    }
}
