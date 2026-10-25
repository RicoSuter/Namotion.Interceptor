using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Mcp.Tests.Tools.Access;
using System.Text.Json;
using Namotion.Interceptor.Mcp.Tools;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Mcp.Tests.Tools;

public class GetPropertyToolTests
{
    [Fact]
    public async Task WhenGettingProperty_ThenReturnsValueAndType()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room = new TestRoom(context) { Name = "Test", Temperature = 21.5m };

        var config = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance,
            IsReadOnly = false
        };
        var factory = new McpToolFactory(room, config);
        var tool = factory.CreateTools().First(t => t.Name == "get_property");

        // Act
        var input = JsonSerializer.SerializeToElement(new { path = "Temperature" });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        Assert.Equal(21.5m, json.GetProperty("value").GetDecimal());
        Assert.Equal("number", json.GetProperty("type").GetString());
        Assert.True(json.GetProperty("isWritable").GetBoolean());
    }

    [Fact]
    public async Task WhenInvalidPath_ThenReturnsError()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room = new TestRoom(context) { Name = "Test", Temperature = 21.5m };

        var config = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance
        };
        var factory = new McpToolFactory(room, config);
        var tool = factory.CreateTools().First(t => t.Name == "get_property");

        // Act
        var input = JsonSerializer.SerializeToElement(new { path = "NonExistent" });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        Assert.True(json.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task WhenThePolicyDeniesWrite_ThenIsWritableIsOmitted()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room = new TestRoom(context) { Name = "Test", Temperature = 21.5m };
        var config = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance,
            IsReadOnly = false,
            AccessPolicy = TestAccessPolicy.DenyWrite("Temperature")
        };
        var tool = new McpToolFactory(room, config).CreateTools().First(t => t.Name == "get_property");

        // Act
        var refused = JsonSerializer.SerializeToElement(await tool.Handler(
            JsonSerializer.SerializeToElement(new { path = "Temperature" }), CancellationToken.None));
        var allowed = JsonSerializer.SerializeToElement(await tool.Handler(
            JsonSerializer.SerializeToElement(new { path = "Name" }), CancellationToken.None));

        // Assert
        Assert.False(refused.TryGetProperty("isWritable", out _));
        Assert.True(allowed.GetProperty("isWritable").GetBoolean());
    }

    [Fact]
    public async Task WhenReadOnly_ThenIsWritableIsOmitted()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room = new TestRoom(context) { Name = "Test", Temperature = 21.5m };

        var config = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance,
            IsReadOnly = true
        };
        var factory = new McpToolFactory(room, config);
        var tool = factory.CreateTools().First(t => t.Name == "get_property");

        // Act
        var input = JsonSerializer.SerializeToElement(new { path = "Temperature" });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        Assert.False(json.TryGetProperty("isWritable", out _));
    }

    [Fact]
    public async Task WhenSetterIsNotPublic_ThenIsWritableIsOmitted()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var sensor = new TestSensor(context) { Reading = 42m };

        var config = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance,
            IsReadOnly = false
        };
        var factory = new McpToolFactory(sensor, config);
        var tool = factory.CreateTools().First(t => t.Name == "get_property");

        // Act
        var input = JsonSerializer.SerializeToElement(new { path = "Reading" });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        Assert.Equal(42m, json.GetProperty("value").GetDecimal());
        Assert.False(json.TryGetProperty("isWritable", out _));
    }

    [Fact]
    public async Task WhenPropertyHasAttributes_ThenAttributesAreIncluded()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room = new TestRoom(context) { Name = "Test", Temperature = 21.5m };

        var registered = room.TryGetRegisteredSubject()!;
        var temperatureProperty = registered.TryGetProperty("Temperature")!;
        temperatureProperty.AddAttribute<string>("Unit", _ => "Celsius");

        var config = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance
        };
        var factory = new McpToolFactory(room, config);
        var tool = factory.CreateTools().First(t => t.Name == "get_property");

        // Act
        var input = JsonSerializer.SerializeToElement(new { path = "Temperature" });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        Assert.True(json.TryGetProperty("attributes", out var attributes));
        Assert.Equal("Celsius", attributes.GetProperty("Unit").GetString());
    }

    [Fact]
    public async Task WhenAReadOnlyAttributeHasNoValue_ThenItIsLeftOutAndAWritableOneIsKept()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room = new TestRoom(context) { Name = "Test", Temperature = 21.5m };
        var temperatureProperty = room.TryGetRegisteredSubject()!.TryGetProperty("Temperature")!;
        temperatureProperty.AddAttribute<string>("Unit", _ => "Celsius");
        temperatureProperty.AddAttribute<string>("Note", _ => null);
        temperatureProperty.AddAttribute("Comment", typeof(string), _ => null, (_, _) => { });

        var config = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance
        };
        var tool = new McpToolFactory(room, config).CreateTools().First(t => t.Name == "get_property");

        // Act
        var input = JsonSerializer.SerializeToElement(new { path = "Temperature" });
        var json = JsonSerializer.SerializeToElement(await tool.Handler(input, CancellationToken.None));

        // Assert
        var attributes = json.GetProperty("attributes");
        Assert.Equal("Celsius", attributes.GetProperty("Unit").GetString());
        Assert.False(attributes.TryGetProperty("Note", out _));
        Assert.Equal(JsonValueKind.Null, attributes.GetProperty("Comment").ValueKind);
    }
}
