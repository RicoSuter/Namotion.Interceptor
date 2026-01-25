using Namotion.Interceptor.OpcUa.Mapping;
using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Tests.Mapping;

public class OpcUaNodeConfigurationTests
{
    [Fact]
    public void WithFallback_WhenThisHasValue_KeepsThisValue()
    {
        // Arrange
        var config1 = new OpcUaNodeConfiguration { BrowseName = "First", SamplingInterval = 100 };
        var config2 = new OpcUaNodeConfiguration { BrowseName = "Second", SamplingInterval = 200 };

        // Act
        var result = config1.WithFallback(config2);

        // Assert
        Assert.Equal("First", result.BrowseName);
        Assert.Equal(100, result.SamplingInterval);
    }

    [Fact]
    public void WithFallback_WhenThisHasNull_TakesOtherValue()
    {
        // Arrange
        var config1 = new OpcUaNodeConfiguration { BrowseName = "First" };
        var config2 = new OpcUaNodeConfiguration { SamplingInterval = 200, QueueSize = 10 };

        // Act
        var result = config1.WithFallback(config2);

        // Assert
        Assert.Equal("First", result.BrowseName);
        Assert.Equal(200, result.SamplingInterval);
        Assert.Equal(10u, result.QueueSize);
    }

    [Fact]
    public void WithFallback_WhenOtherIsNull_ReturnsThis()
    {
        // Arrange
        var config = new OpcUaNodeConfiguration { BrowseName = "Test" };

        // Act
        var result = config.WithFallback(null);

        // Assert
        Assert.Same(config, result);
    }

    [Fact]
    public void WithFallback_MergesAllFields()
    {
        // Arrange
        var config1 = new OpcUaNodeConfiguration
        {
            BrowseName = "Name1",
            DataChangeTrigger = DataChangeTrigger.Status
        };
        var config2 = new OpcUaNodeConfiguration
        {
            BrowseName = "Name2",
            BrowseNamespaceUri = "http://test/",
            NodeIdentifier = "Node1",
            TypeDefinition = "BaseType",
            SamplingInterval = 500,
            ModellingRule = ModellingRule.Mandatory
        };

        // Act
        var result = config1.WithFallback(config2);

        // Assert
        Assert.Equal("Name1", result.BrowseName); // config1 wins
        Assert.Equal("http://test/", result.BrowseNamespaceUri); // from config2
        Assert.Equal("Node1", result.NodeIdentifier); // from config2
        Assert.Equal("BaseType", result.TypeDefinition); // from config2
        Assert.Equal(500, result.SamplingInterval); // from config2
        Assert.Equal(DataChangeTrigger.Status, result.DataChangeTrigger); // config1 wins
        Assert.Equal(ModellingRule.Mandatory, result.ModellingRule); // from config2
    }

    [Fact]
    public void WithFallback_MergesMonitoringFields()
    {
        // Arrange
        var config1 = new OpcUaNodeConfiguration
        {
            SamplingInterval = 100,
            DeadbandType = DeadbandType.Absolute
        };
        var config2 = new OpcUaNodeConfiguration
        {
            QueueSize = 5,
            DiscardOldest = false,
            DeadbandValue = 0.5
        };

        // Act
        var result = config1.WithFallback(config2);

        // Assert
        Assert.Equal(100, result.SamplingInterval);
        Assert.Equal(5u, result.QueueSize);
        Assert.False(result.DiscardOldest);
        Assert.Equal(DeadbandType.Absolute, result.DeadbandType);
        Assert.Equal(0.5, result.DeadbandValue);
    }

    [Fact]
    public void WithFallback_MergesAdditionalReferences()
    {
        // Arrange
        var refs1 = new List<OpcUaAdditionalReference>
        {
            new() { ReferenceType = "HasInterface", TargetNodeId = "i=17602" }
        };
        var refs2 = new List<OpcUaAdditionalReference>
        {
            new() { ReferenceType = "GeneratesEvent", TargetNodeId = "ns=2;s=Event" }
        };

        var config1 = new OpcUaNodeConfiguration { AdditionalReferences = refs1 };
        var config2 = new OpcUaNodeConfiguration { AdditionalReferences = refs2 };

        // Act
        var result = config1.WithFallback(config2);

        // Assert - config1's AdditionalReferences wins (not merged together)
        Assert.NotNull(result.AdditionalReferences);
        Assert.Single(result.AdditionalReferences);
        Assert.Equal("HasInterface", result.AdditionalReferences[0].ReferenceType);
    }

    [Fact]
    public void WithFallback_WhenThisHasNoAdditionalReferences_TakesOther()
    {
        // Arrange
        var refs = new List<OpcUaAdditionalReference>
        {
            new() { ReferenceType = "HasInterface", TargetNodeId = "i=17602" }
        };

        var config1 = new OpcUaNodeConfiguration { BrowseName = "Test" };
        var config2 = new OpcUaNodeConfiguration { AdditionalReferences = refs };

        // Act
        var result = config1.WithFallback(config2);

        // Assert
        Assert.NotNull(result.AdditionalReferences);
        Assert.Single(result.AdditionalReferences);
        Assert.Equal("HasInterface", result.AdditionalReferences[0].ReferenceType);
    }
}
