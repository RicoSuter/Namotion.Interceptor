using System.Text.Json;
using Namotion.Interceptor.Mcp.Tools;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;
using Xunit;

namespace Namotion.Interceptor.Mcp.Tests.Tools;

public class PathResolutionTests
{
    [Fact]
    public async Task Query_resolves_subject_through_dictionary_path()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var server = new TestContainer(context) { Name = "OpcUaServer" };
        var servers = new TestContainer(context) { Name = "Servers" };
        servers.Children["OpcUaServer"] = server;
        var root = new TestContainer(context) { Name = "Root" };
        root.Children["Servers"] = servers;

        var config = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance
        };
        var factory = new McpToolFactory(root, config);
        var queryTool = factory.CreateTools().First(tool => tool.Name == "query");

        // Act - query with path through two dictionary levels
        var input = JsonSerializer.SerializeToElement(new
        {
            path = "Children[Servers].Children[OpcUaServer]",
            depth = 1,
            includeProperties = true
        });
        var result = await queryTool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert - should resolve to the OpcUaServer subject
        Assert.False(json.TryGetProperty("error", out _), "Expected no error");
        var subjects = json.GetProperty("subjects");
        Assert.True(subjects.TryGetProperty("Name", out var nameProperty));
        Assert.Equal("OpcUaServer", nameProperty.GetProperty("value").GetString());
    }

    [Fact]
    public async Task GetProperty_resolves_through_dictionary_path()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var server = new TestContainer(context) { Name = "OpcUaServer" };
        var servers = new TestContainer(context) { Name = "Servers" };
        servers.Children["OpcUaServer"] = server;
        var root = new TestContainer(context) { Name = "Root" };
        root.Children["Servers"] = servers;

        var config = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance
        };
        var factory = new McpToolFactory(root, config);
        var tool = factory.CreateTools().First(tool => tool.Name == "get_property");

        // Act - get property through dictionary path
        var input = JsonSerializer.SerializeToElement(new
        {
            path = "Children[Servers].Children[OpcUaServer].Name"
        });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        Assert.False(json.TryGetProperty("error", out _), "Expected no error");
        Assert.Equal("OpcUaServer", json.GetProperty("value").GetString());
    }

    [Fact]
    public async Task SetProperty_resolves_through_dictionary_path()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var server = new TestContainer(context) { Name = "OpcUaServer" };
        var servers = new TestContainer(context) { Name = "Servers" };
        servers.Children["OpcUaServer"] = server;
        var root = new TestContainer(context) { Name = "Root" };
        root.Children["Servers"] = servers;

        var config = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance,
            IsReadOnly = false
        };
        var factory = new McpToolFactory(root, config);
        var tool = factory.CreateTools().First(tool => tool.Name == "set_property");

        // Act - set property through dictionary path
        var input = JsonSerializer.SerializeToElement(new
        {
            path = "Children[Servers].Children[OpcUaServer].Name",
            value = "Renamed"
        });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        Assert.True(json.GetProperty("success").GetBoolean());
        Assert.Equal("Renamed", server.Name);
    }

    [Fact]
    public void TryGetSubjectFromPath_resolves_through_nested_dictionaries()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var leaf = new TestContainer(context) { Name = "Leaf" };
        var middle = new TestContainer(context) { Name = "Middle" };
        middle.Children["Leaf"] = leaf;
        var root = new TestContainer(context) { Name = "Root" };
        root.Children["Middle"] = middle;

        var pathProvider = DefaultPathProvider.Instance;
        var rootRegistered = root.TryGetRegisteredSubject()!;

        // Act
        var resolved = pathProvider.TryGetSubjectFromPath(rootRegistered, "Children[Middle].Children[Leaf]");

        // Assert
        Assert.NotNull(resolved);
        Assert.Same(leaf, resolved.Subject);
    }

    [Fact]
    public void TryGetSubjectFromPath_resolves_subject_reference()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var device = new TestDevice(context) { DeviceName = "Light", IsOn = true };
        var room = new TestRoom(context) { Name = "Room", Device = device };

        var pathProvider = DefaultPathProvider.Instance;
        var rootRegistered = room.TryGetRegisteredSubject()!;

        // Act
        var resolved = pathProvider.TryGetSubjectFromPath(rootRegistered, "Device");

        // Assert
        Assert.NotNull(resolved);
        Assert.Same(device, resolved.Subject);
    }

    [Fact]
    public void TryGetSubjectFromPath_returns_null_for_invalid_path()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var root = new TestContainer(context) { Name = "Root" };

        var pathProvider = DefaultPathProvider.Instance;
        var rootRegistered = root.TryGetRegisteredSubject()!;

        // Act
        var resolved = pathProvider.TryGetSubjectFromPath(rootRegistered, "Children[NonExistent]");

        // Assert
        Assert.Null(resolved);
    }
}
