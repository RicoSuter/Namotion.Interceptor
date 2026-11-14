using HomeBlaze.Services;
using HomeBlaze.Virtual.Tests.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomeBlaze.Virtual.Tests;

public class VirtualSubjectConfigurationTests
{
    [Fact]
    public void WhenAVirtualSubjectIsSerialized_ThenTheEntriesAreWrittenInTheCanonicalForm()
    {
        // Arrange
        var graph = new VirtualTestGraph();
        var solar = graph.CreateVirtualSubject(new Dictionary<string, VirtualProperty>
        {
            ["Power"] = new() { Path = "/Meter/Power", DataType = VirtualDataType.Number, State = new VirtualState { Title = "PV" } }
        });
        graph.Add("Solar", solar);

        // Act
        var json = CreateSerializer(graph).Serialize(solar);

        // Assert
        Assert.Contains("\"properties\": {", json);
        Assert.Contains("\"path\": \"/Meter/Power\"", json);
        Assert.Contains("\"dataType\": \"Number\"", json);
        Assert.Contains("\"title\": \"PV\"", json);
        Assert.DoesNotContain("\"expression\"", json);
    }

    [Fact]
    public void WhenAVirtualSubjectIsDeserialized_ThenItHasTheEntries()
    {
        // Arrange
        var graph = new VirtualTestGraph();
        var json = $$"""
            { "$type": "{{typeof(VirtualSubject).FullName}}", "title": "Solar",
              "properties": { "Pv": { "properties": { "Double": { "expression": "1 * 2", "dataType": "Number" } } } } }
            """;

        // Act
        var solar = Assert.IsType<VirtualSubject>(CreateSerializer(graph).Deserialize(json));

        // Assert
        Assert.Equal("Solar", solar.Title);
        Assert.Equal("1 * 2", solar.Properties!["Pv"].Properties!["Double"].Expression);
    }

    [Fact]
    public void WhenThePropertiesChange_ThenTheVirtualSubjectIsRecreated()
    {
        // Arrange
        var graph = new VirtualTestGraph();
        var solar = graph.CreateVirtualSubject(new Dictionary<string, VirtualProperty>());
        graph.Add("Solar", solar);
        var type = typeof(VirtualSubject).FullName;

        // Act
        var result = ConfigurationUpdatePolicy.CanUpdateInPlace(solar, typeof(VirtualSubject),
            $$"""{ "$type": "{{type}}", "title": "a", "properties": { "B": { "expression": "1", "dataType": "Number" } } }""",
            $$"""{ "$type": "{{type}}", "title": "a", "properties": {} }""", null, ConfigurationNaming.JsonProperty);

        // Assert
        Assert.False(result);
    }

    private static ConfigurableSubjectSerializer CreateSerializer(VirtualTestGraph graph)
    {
        var services = new ServiceCollection()
            .AddSingleton(graph.Resolver)
            .AddSingleton(graph.Awaiter)
            .AddSingleton<ILogger<VirtualSubject>>(NullLogger<VirtualSubject>.Instance)
            .BuildServiceProvider();
        var typeProvider = new TypeProvider();
        typeProvider.AddTypes([typeof(VirtualSubject)]);
        return new ConfigurableSubjectSerializer(typeProvider, services);
    }
}
