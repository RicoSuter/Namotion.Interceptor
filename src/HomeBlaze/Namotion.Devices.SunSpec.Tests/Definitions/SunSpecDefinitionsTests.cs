using System.Text;
using System.Text.Json;
using Namotion.Devices.SunSpec.Definitions;

namespace Namotion.Devices.SunSpec.Tests.Definitions;

public class SunSpecDefinitionsTests
{
    [Fact]
    public void WhenListingBuiltInIds_ThenEveryEmbeddedDefinitionIsListed()
    {
        // Act
        var modelIds = SunSpecDefinitions.GetBuiltInModelIds();

        // Assert
        Assert.Equal(112, modelIds.Count);
        Assert.Contains(1, modelIds);
        Assert.Contains(713, modelIds);
    }

    [Fact]
    public void WhenParsingEveryBuiltInDefinition_ThenEachParses()
    {
        // Act & Assert
        foreach (var modelId in SunSpecDefinitions.GetBuiltInModelIds())
        {
            var definition = SunSpecDefinitions.TryGetBuiltIn(modelId);
            Assert.NotNull(definition);
            Assert.Equal(modelId, definition.Id);
        }
    }

    [Fact]
    public void WhenParsingModel160_ThenRepeatingGroupAndScaleFactorsAreRead()
    {
        // Act
        var definition = SunSpecDefinitions.TryGetBuiltIn(160)!;

        // Assert
        var module = Assert.Single(definition.Group.Groups);
        Assert.Equal("module", module.Name);
        Assert.True(module.Count.IsFill);
        Assert.Equal("DCA_SF", module.Points.Single(point => point.Name == "DCA").ScaleFactor.PointName);
    }

    [Fact]
    public void WhenParsingModel705_ThenCountPointsAreRead()
    {
        // Act
        var definition = SunSpecDefinitions.TryGetBuiltIn(705)!;

        // Assert
        var curve = Assert.Single(definition.Group.Groups);
        Assert.Equal("NCrv", curve.Count.PointName);
        Assert.Equal("NPt", Assert.Single(curve.Groups).Count.PointName);
    }

    [Fact]
    public void WhenModelIsUnknown_ThenNoBuiltInDefinitionIsReturned()
    {
        // Act
        var definition = SunSpecDefinitions.TryGetBuiltIn(64999);

        // Assert
        Assert.Null(definition);
    }

    [Fact]
    public void WhenDefinitionDoesNotStartWithIdAndLength_ThenParsingFails()
    {
        // Arrange
        const string json = """{ "id": 64999, "group": { "name": "x", "points": [ { "name": "W", "type": "int16", "size": 1 } ] } }""";

        // Act & Assert
        Assert.Throws<JsonException>(() => SunSpecDefinitions.Parse(new MemoryStream(Encoding.UTF8.GetBytes(json))));
    }
}
