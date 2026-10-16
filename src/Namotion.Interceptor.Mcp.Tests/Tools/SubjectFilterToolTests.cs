using System.Text.Json;
using Namotion.Interceptor.Mcp.Tools;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Mcp.Tests.Tools;

public class SubjectFilterToolTests
{
    [Fact]
    public async Task WhenSearchHasWhere_ThenReturnsOnlyMatchingSubjects()
    {
        // Arrange
        var tool = CreateTool(CreateGroup(), "search");

        // Act
        var json = await InvokeAsync(tool, new { format = "json", where = "IsOn == false", properties = new[] { "DeviceName" } });

        // Assert
        Assert.Equal(2, json.GetProperty("subjectCount").GetInt32());
        var names = json.GetProperty("results").EnumerateObject()
            .Select(result => result.Value.GetProperty("properties").GetProperty("DeviceName").GetProperty("value").GetString()!)
            .OrderBy(name => name)
            .ToArray();
        Assert.Equal(["Heater", "Pump"], names);
    }

    [Fact]
    public async Task WhenSearchWhereIsInvalid_ThenReturnsError()
    {
        // Arrange
        var tool = CreateTool(CreateGroup(), "search");

        // Act
        var json = await InvokeAsync(tool, new { format = "json", where = "IsOn = false" });

        // Assert
        Assert.Equal("where: use '==' for equality at position 5", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task WhenSearchWhereIsNotString_ThenReturnsError()
    {
        // Arrange
        var tool = CreateTool(CreateGroup(), "search");

        // Act
        var json = await InvokeAsync(tool, new { format = "json", where = 1 });

        // Assert
        Assert.Equal("Parameter 'where' must be a string.", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task WhenSearchHasProperties_ThenReturnsOnlySelectedProperties()
    {
        // Arrange
        var tool = CreateTool(CreateGroup(), "search");

        // Act
        var json = await InvokeAsync(tool, new { format = "json", types = new[] { typeof(TestDevice).FullName }, properties = new[] { "IsOn" } });

        // Assert
        Assert.Equal(3, json.GetProperty("subjectCount").GetInt32());
        foreach (var result in json.GetProperty("results").EnumerateObject())
        {
            var propertyNames = result.Value.GetProperty("properties").EnumerateObject().Select(property => property.Name).ToArray();
            Assert.Equal(["IsOn"], propertyNames);
        }
    }

    [Fact]
    public async Task WhenPropertiesIsNotArray_ThenReturnsError()
    {
        // Arrange
        var tool = CreateTool(CreateGroup(), "search");

        // Act
        var json = await InvokeAsync(tool, new { format = "json", properties = "IsOn" });

        // Assert
        Assert.Equal("Parameter 'properties' must be an array of property names.", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task WhenBrowseHasProperties_ThenChildrenShowOnlySelectedProperties()
    {
        // Arrange
        var tool = CreateTool(CreateGroup(), "browse");

        // Act
        var json = await InvokeAsync(tool, new { format = "json", depth = 1, properties = new[] { "IsOn" } });

        // Assert
        var children = json.GetProperty("result").GetProperty("properties").GetProperty("Devices").GetProperty("children").EnumerateObject().ToArray();
        Assert.Equal(3, children.Length);
        foreach (var child in children)
        {
            var propertyNames = child.Value.GetProperty("properties").EnumerateObject().Select(property => property.Name).ToArray();
            Assert.Equal(["IsOn"], propertyNames);
        }
    }

    [Fact]
    public async Task WhenSearchHasWhereInTextFormat_ThenListsOnlyMatchingSubjects()
    {
        // Arrange
        var tool = CreateTool(CreateGroup(), "search");

        // Act
        var result = await tool.Handler(
            JsonSerializer.SerializeToElement(new { where = "DeviceName == Light", properties = new[] { "IsOn" } }),
            CancellationToken.None);

        // Assert
        var text = Assert.IsType<string>(result);
        Assert.Contains("IsOn: true", text);
        Assert.DoesNotContain("Heater", text);
        Assert.EndsWith("[1 subject]", text);
    }

    private static TestDeviceGroup CreateGroup()
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var group = new TestDeviceGroup(context);
        group.Devices = new Dictionary<string, TestDevice>
        {
            ["Light"] = new(context) { DeviceName = "Light", IsOn = true },
            ["Heater"] = new(context) { DeviceName = "Heater", IsOn = false },
            ["Pump"] = new(context) { DeviceName = "Pump", IsOn = false }
        };
        return group;
    }

    private static McpToolInfo CreateTool(IInterceptorSubject root, string name)
    {
        var configuration = new McpServerConfiguration { PathProvider = DefaultPathProvider.Instance };
        return new McpToolFactory(root, configuration).CreateTools().First(tool => tool.Name == name);
    }

    private static async Task<JsonElement> InvokeAsync(McpToolInfo tool, object input)
    {
        var result = await tool.Handler(JsonSerializer.SerializeToElement(input), CancellationToken.None);
        return JsonSerializer.SerializeToElement(result);
    }
}
