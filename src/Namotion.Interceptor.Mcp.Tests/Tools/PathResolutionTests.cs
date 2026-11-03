using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Namotion.Interceptor.Mcp.Tools;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Mcp.Tests.Tools;

public class PathResolutionTests
{
    [Theory]
    [InlineData("get_property")]
    [InlineData("set_property")]
    [InlineData("browse")]
    public async Task WhenPathSyntaxIsInvalid_ThenToolReturnsSyntaxError(string toolName)
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var root = new TestContainer(context) { Name = "Root" };
        var config = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance,
            IsReadOnly = false
        };
        var tool = new McpToolFactory(root, config).CreateTools().First(t => t.Name == toolName);
        var input = JsonSerializer.SerializeToElement(new { path = "Children[Servers", value = "x" });

        // Act
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        Assert.Equal(
            "Invalid path syntax: Unclosed '[' at position 8 in path 'Children[Servers'",
            json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task WhenSubjectIsHeldBelowAPropertyTheProviderExcludes_ThenSearchAndBrowseDoNotListIt()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var device = new TestDevice(context) { DeviceName = "Lamp" };
        var room = new TestRoom(context) { Name = "Kitchen", Device = device };
        var root = new TestMixedGroup(context) { Items = new Dictionary<string, IInterceptorSubject> { ["room"] = room } };

        var pathProvider = new ExcludingPathProvider(nameof(TestMixedGroup.Items));
        var config = new McpServerConfiguration { PathProvider = pathProvider };
        var tools = new McpToolFactory(root, config).CreateTools();

        // Act
        var search = JsonSerializer.SerializeToElement(await tools.First(t => t.Name == "search").Handler(
            JsonSerializer.SerializeToElement(new { format = "json", types = new[] { nameof(TestDevice) } }), CancellationToken.None));
        var browse = JsonSerializer.SerializeToElement(await tools.First(t => t.Name == "browse").Handler(
            JsonSerializer.SerializeToElement(new { format = "json", depth = 5 }), CancellationToken.None));
        var path = McpToolHelper.TryGetSubjectPath(device.TryGetRegisteredSubject()!, config, root);

        // Assert
        Assert.Empty(search.GetProperty("results").EnumerateObject());
        Assert.Equal(0, browse.GetProperty("subjectCount").GetInt32());
        Assert.Null(path);
    }

    [Theory]
    [InlineData("get_property", "Items[room].Device.DeviceName")]
    [InlineData("set_property", "Items[room].Device.DeviceName")]
    [InlineData("browse", "Items[room].Device")]
    public async Task WhenPathPassesAPropertyTheProviderExcludes_ThenToolAnswersPathNotFound(string toolName, string path)
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var device = new TestDevice(context) { DeviceName = "Lamp" };
        var room = new TestRoom(context) { Name = "Kitchen", Device = device };
        var root = new TestMixedGroup(context) { Items = new Dictionary<string, IInterceptorSubject> { ["room"] = room } };

        var config = new McpServerConfiguration
        {
            PathProvider = new ExcludingPathProvider(nameof(TestMixedGroup.Items)),
            IsReadOnly = false
        };
        var tool = new McpToolFactory(root, config).CreateTools().First(t => t.Name == toolName);

        // Act
        var result = JsonSerializer.SerializeToElement(await tool.Handler(
            JsonSerializer.SerializeToElement(new { path, value = "Changed" }), CancellationToken.None));

        // Assert
        Assert.Equal($"Path not found: {path}", result.GetProperty("error").GetString());
        Assert.Equal("Lamp", device.DeviceName);
    }

    [Fact]
    public async Task WhenSubjectsFirstParentIsExcludedAndSecondIsIncluded_ThenToolsReturnAndResolveThePathThroughTheSecond()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var room = new TestRoom(context) { Name = "Kitchen" };
        var group = new TestDeviceGroup(context);
        var root = new TestMixedGroup(context)
        {
            Items = new Dictionary<string, IInterceptorSubject> { ["room"] = room, ["group"] = group }
        };

        var device = new TestDevice(context) { DeviceName = "Lamp" };
        room.Device = device;
        group.Devices = new Dictionary<string, TestDevice> { ["lamp"] = device };

        var config = new McpServerConfiguration { PathProvider = new ExcludingPathProvider(nameof(TestRoom.Device)) };
        var tools = new McpToolFactory(root, config).CreateTools();

        // Act
        var search = JsonSerializer.SerializeToElement(await tools.First(t => t.Name == "search").Handler(
            JsonSerializer.SerializeToElement(new { format = "json", types = new[] { nameof(TestDevice) } }), CancellationToken.None));
        var property = JsonSerializer.SerializeToElement(await tools.First(t => t.Name == "get_property").Handler(
            JsonSerializer.SerializeToElement(new { path = "Items[group].Devices[lamp].DeviceName" }), CancellationToken.None));
        var browse = JsonSerializer.SerializeToElement(await tools.First(t => t.Name == "browse").Handler(
            JsonSerializer.SerializeToElement(new { format = "json", path = "Items[group].Devices[lamp]", depth = 0, includeProperties = true }), CancellationToken.None));

        // Assert
        Assert.Equal(nameof(TestRoom.Device), device.TryGetRegisteredSubject()!.Parents[0].Property.Name);
        var result = Assert.Single(search.GetProperty("results").EnumerateObject());
        Assert.Equal("Items[group].Devices[lamp]", result.Name);
        Assert.Equal("Items[group].Devices[lamp]", result.Value.GetProperty("$path").GetString());
        Assert.Equal("Lamp", property.GetProperty("value").GetString());
        Assert.Equal("Items[group].Devices[lamp]", browse.GetProperty("result").GetProperty("$path").GetString());
        Assert.Equal("Lamp", browse.GetProperty("result").GetProperty("properties").GetProperty("DeviceName").GetProperty("value").GetString());
    }

    [Fact]
    public async Task WhenSubjectIsHeldByAnAttributeOfAPropertyTheProviderExcludes_ThenSearchDoesNotListIt()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var root = new TestAttributedRoom(context)
        {
            Name = "Kitchen",
            Name_Spare = new TestDevice { DeviceName = "Spare" },
            Device = new TestDevice { DeviceName = "Lamp" }
        };

        var config = new McpServerConfiguration { PathProvider = new ExcludingPathProvider(nameof(TestAttributedRoom.Name)) };
        var search = new McpToolFactory(root, config).CreateTools().First(t => t.Name == "search");

        // Act
        var result = JsonSerializer.SerializeToElement(await search.Handler(
            JsonSerializer.SerializeToElement(new { format = "json", types = new[] { nameof(TestDevice) } }), CancellationToken.None));

        // Assert
        var found = Assert.Single(result.GetProperty("results").EnumerateObject());
        Assert.Equal(nameof(TestAttributedRoom.Device), found.Name);
    }

    private sealed class ExcludingPathProvider(string excludedPropertyName) : PathProviderBase
    {
        public override bool TryGetPropertySegment(RegisteredSubjectProperty property, [NotNullWhen(true)] out string? segment)
        {
            segment = property.Name != excludedPropertyName ? property.BrowseName : null;
            return segment is not null;
        }
    }

    [Fact]
    public async Task WhenQueryWithDictionaryPath_ThenSubjectIsResolved()
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
        var queryTool = factory.CreateTools().First(tool => tool.Name == "browse");

        // Act - query with path through two dictionary levels
        var input = JsonSerializer.SerializeToElement(new
        {
            format = "json",
            path = "Children[Servers].Children[OpcUaServer]",
            depth = 1,
            includeProperties = true
        });
        var result = await queryTool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert - should resolve to the OpcUaServer subject
        Assert.False(json.TryGetProperty("error", out _), "Expected no error");
        var resultNode = json.GetProperty("result");
        var properties = resultNode.GetProperty("properties");
        var nameProperty = properties.GetProperty("Name");
        Assert.Equal("OpcUaServer", nameProperty.GetProperty("value").GetString());
    }

    [Fact]
    public async Task WhenGetPropertyWithDictionaryPath_ThenPropertyIsResolved()
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
    public async Task WhenSetPropertyWithDictionaryPath_ThenPropertyIsUpdated()
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
    public void WhenTryGetSubjectFromPathWithNestedDictionaries_ThenSubjectIsResolved()
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
    public void WhenTryGetSubjectFromPathWithSubjectReference_ThenSubjectIsResolved()
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
    public void WhenTryGetSubjectFromPathWithInvalidPath_ThenReturnsNull()
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
