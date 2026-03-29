using System.Text.Json;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Mcp.Tools;
using Namotion.Interceptor.Registry.Paths;
using Xunit;

namespace Namotion.Interceptor.Mcp.Tests.Tools;

public class ListTypesToolTests
{
    [Fact]
    public async Task ListTypes_interface_includes_properties()
    {
        // Arrange
        var typeProvider = new TestTypeProvider(
            new McpTypeInfo("ITestInterface", "Test", IsInterface: true, Type: typeof(ITestInterface)));

        var config = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance,
            TypeProviders = { typeProvider }
        };
        var factory = new McpToolFactory(() => null!, config);
        var tool = factory.CreateTools().First(t => t.Name == "list_types");

        // Act
        var input = JsonSerializer.SerializeToElement(new { });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        var types = json.GetProperty("types");
        var firstType = types.EnumerateArray().First();
        Assert.True(firstType.TryGetProperty("properties", out var properties));
        Assert.Contains(properties.EnumerateArray().ToArray(), p => p.GetProperty("name").GetString() == "Speed");
    }

    [Fact]
    public async Task ListTypes_concrete_includes_known_interfaces()
    {
        // Arrange — register both the interface and the concrete type
        var typeProvider = new TestTypeProvider(
            new McpTypeInfo("ITestInterface", "Test", IsInterface: true, Type: typeof(ITestInterface)),
            new McpTypeInfo("TestMotor", "Test motor", IsInterface: false, Type: typeof(TestMotor)));

        var config = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance,
            TypeProviders = { typeProvider }
        };
        var factory = new McpToolFactory(() => null!, config);
        var tool = factory.CreateTools().First(t => t.Name == "list_types");

        // Act
        var input = JsonSerializer.SerializeToElement(new { });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert — concrete type lists known interfaces, not properties
        var concreteType = json.GetProperty("types").EnumerateArray()
            .First(t => t.GetProperty("name").GetString() == "TestMotor");
        Assert.True(concreteType.TryGetProperty("interfaces", out var interfaces));
        Assert.False(concreteType.TryGetProperty("properties", out _));

        var interfaceNames = interfaces.EnumerateArray().Select(i => i.GetString()).ToArray();
        Assert.Contains(typeof(ITestInterface).FullName, interfaceNames);
    }

    public interface ITestInterface
    {
        string Speed { get; set; }
        string Name { get; }
    }

    public class TestMotor : ITestInterface
    {
        public string Speed { get; set; } = "";
        public string Name { get; } = "";
    }

    private class TestTypeProvider : IMcpTypeProvider
    {
        private readonly McpTypeInfo[] _types;
        public TestTypeProvider(params McpTypeInfo[] types) => _types = types;
        public IEnumerable<McpTypeInfo> GetTypes() => _types;
    }
}
