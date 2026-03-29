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

    [Fact]
    public async Task ListTypes_kind_filters_to_interfaces_only()
    {
        // Arrange
        var typeProvider = new TestTypeProvider(
            new McpTypeInfo("ITestInterface", "Test", IsInterface: true, Type: typeof(ITestInterface)),
            new McpTypeInfo("TestMotor", "Motor", IsInterface: false, Type: typeof(TestMotor)));

        var config = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance,
            TypeProviders = { typeProvider }
        };
        var factory = new McpToolFactory(() => null!, config);
        var tool = factory.CreateTools().First(t => t.Name == "list_types");

        // Act
        var input = JsonSerializer.SerializeToElement(new { kind = "interfaces" });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        var types = json.GetProperty("types");
        Assert.All(types.EnumerateArray().ToArray(),
            t => Assert.True(t.GetProperty("isInterface").GetBoolean()));
    }

    [Fact]
    public async Task ListTypes_kind_filters_to_concrete_only()
    {
        // Arrange
        var typeProvider = new TestTypeProvider(
            new McpTypeInfo("ITestInterface", "Test", IsInterface: true, Type: typeof(ITestInterface)),
            new McpTypeInfo("TestMotor", "Motor", IsInterface: false, Type: typeof(TestMotor)));

        var config = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance,
            TypeProviders = { typeProvider }
        };
        var factory = new McpToolFactory(() => null!, config);
        var tool = factory.CreateTools().First(t => t.Name == "list_types");

        // Act
        var input = JsonSerializer.SerializeToElement(new { kind = "concrete" });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        var types = json.GetProperty("types");
        Assert.All(types.EnumerateArray().ToArray(),
            t => Assert.False(t.GetProperty("isInterface").GetBoolean()));
    }

    [Fact]
    public async Task ListTypes_kind_all_returns_everything()
    {
        // Arrange
        var typeProvider = new TestTypeProvider(
            new McpTypeInfo("ITestInterface", "Test", IsInterface: true, Type: typeof(ITestInterface)),
            new McpTypeInfo("TestMotor", "Motor", IsInterface: false, Type: typeof(TestMotor)));

        var config = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance,
            TypeProviders = { typeProvider }
        };
        var factory = new McpToolFactory(() => null!, config);
        var tool = factory.CreateTools().First(t => t.Name == "list_types");

        // Act
        var input = JsonSerializer.SerializeToElement(new { kind = "all" });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        var types = json.GetProperty("types");
        Assert.Equal(2, types.EnumerateArray().Count());
    }

    [Fact]
    public async Task ListTypes_type_search_filters_by_name()
    {
        // Arrange
        var typeProvider = new TestTypeProvider(
            new McpTypeInfo("ITestInterface", "Test", IsInterface: true, Type: typeof(ITestInterface)),
            new McpTypeInfo("TestMotor", "Motor", IsInterface: false, Type: typeof(TestMotor)));

        var config = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance,
            TypeProviders = { typeProvider }
        };
        var factory = new McpToolFactory(() => null!, config);
        var tool = factory.CreateTools().First(t => t.Name == "list_types");

        // Act — search for "Motor"
        var input = JsonSerializer.SerializeToElement(new { type = "Motor" });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert — only TestMotor should match
        var types = json.GetProperty("types");
        Assert.Single(types.EnumerateArray().ToArray());
        Assert.Equal("TestMotor", types.EnumerateArray().First().GetProperty("name").GetString());
    }

    [Fact]
    public async Task ListTypes_type_search_is_case_insensitive()
    {
        // Arrange
        var typeProvider = new TestTypeProvider(
            new McpTypeInfo("ITestInterface", "Test", IsInterface: true, Type: typeof(ITestInterface)),
            new McpTypeInfo("TestMotor", "Motor", IsInterface: false, Type: typeof(TestMotor)));

        var config = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance,
            TypeProviders = { typeProvider }
        };
        var factory = new McpToolFactory(() => null!, config);
        var tool = factory.CreateTools().First(t => t.Name == "list_types");

        // Act — search for "motor" (lowercase)
        var input = JsonSerializer.SerializeToElement(new { type = "motor" });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert — should match TestMotor despite case difference
        var types = json.GetProperty("types");
        Assert.Single(types.EnumerateArray().ToArray());
        Assert.Equal("TestMotor", types.EnumerateArray().First().GetProperty("name").GetString());
    }

    [Fact]
    public async Task ListTypes_kind_and_type_combined()
    {
        // Arrange
        var typeProvider = new TestTypeProvider(
            new McpTypeInfo("ITestInterface", "Test", IsInterface: true, Type: typeof(ITestInterface)),
            new McpTypeInfo("IMotorInterface", "Motor interface", IsInterface: true, Type: typeof(ITestInterface)),
            new McpTypeInfo("TestMotor", "Motor", IsInterface: false, Type: typeof(TestMotor)));

        var config = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance,
            TypeProviders = { typeProvider }
        };
        var factory = new McpToolFactory(() => null!, config);
        var tool = factory.CreateTools().First(t => t.Name == "list_types");

        // Act — search for "Motor" among interfaces only
        var input = JsonSerializer.SerializeToElement(new { kind = "interfaces", type = "Motor" });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert — only the IMotorInterface should match (interface + contains "Motor")
        var types = json.GetProperty("types");
        Assert.Single(types.EnumerateArray().ToArray());
        Assert.Equal("IMotorInterface", types.EnumerateArray().First().GetProperty("name").GetString());
    }

    [Fact]
    public async Task ListTypes_kind_concrete_still_lists_all_known_interfaces()
    {
        // Arrange — both interface and concrete are registered
        var typeProvider = new TestTypeProvider(
            new McpTypeInfo("ITestInterface", "Test", IsInterface: true, Type: typeof(ITestInterface)),
            new McpTypeInfo("TestMotor", "Motor", IsInterface: false, Type: typeof(TestMotor)));

        var config = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance,
            TypeProviders = { typeProvider }
        };
        var factory = new McpToolFactory(() => null!, config);
        var tool = factory.CreateTools().First(t => t.Name == "list_types");

        // Act — filter to concrete only
        var input = JsonSerializer.SerializeToElement(new { kind = "concrete" });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert — TestMotor should still reference ITestInterface even though interfaces are filtered out
        var concreteType = json.GetProperty("types").EnumerateArray().First();
        Assert.True(concreteType.TryGetProperty("interfaces", out var interfaces));
        var interfaceNames = interfaces.EnumerateArray().Select(i => i.GetString()).ToArray();
        Assert.Contains(typeof(ITestInterface).FullName, interfaceNames);
    }

    private class TestTypeProvider : IMcpTypeProvider
    {
        private readonly McpTypeInfo[] _types;
        public TestTypeProvider(params McpTypeInfo[] types) => _types = types;
        public IEnumerable<McpTypeInfo> GetTypes() => _types;
    }
}
