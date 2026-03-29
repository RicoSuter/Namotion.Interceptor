using System.Text.Json;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Mcp.Tools;
using Namotion.Interceptor.Registry.Paths;
using Xunit;

namespace Namotion.Interceptor.Mcp.Tests.Tools;

public class ListTypesToolEdgeCaseTests
{
    [Fact]
    public async Task WhenInterfaceHasMethods_ThenMethodsAreIncludedWithParameters()
    {
        // Arrange
        var typeProvider = new TestTypeProvider(
            new McpTypeInfo("IHasMethod", "With methods", IsInterface: true, Type: typeof(IHasMethod)));

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
        var firstType = json.GetProperty("types").EnumerateArray().First();
        Assert.True(firstType.TryGetProperty("methods", out var methods));

        var doSomething = methods.EnumerateArray()
            .First(m => m.GetProperty("name").GetString() == "DoSomething");
        Assert.Equal("string", doSomething.GetProperty("returnType").GetString());

        var parameters = doSomething.GetProperty("parameters");
        Assert.Contains(parameters.EnumerateArray().ToArray(),
            p => p.GetProperty("name").GetString() == "input" && p.GetProperty("type").GetString() == "string");
    }

    [Fact]
    public async Task WhenInterfaceHasNoMethods_ThenMethodsArrayIsEmpty()
    {
        // Arrange
        var typeProvider = new TestTypeProvider(
            new McpTypeInfo("IEmpty", "No methods", IsInterface: true, Type: typeof(INoMethods)));

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
        var firstType = json.GetProperty("types").EnumerateArray().First();
        var methods = firstType.GetProperty("methods");
        Assert.Equal(0, methods.GetArrayLength());
    }

    [Fact]
    public async Task WhenConcreteTypeHasNoKnownInterfaces_ThenInterfacesArrayIsEmpty()
    {
        // Arrange — only register the concrete type, not the interface
        var typeProvider = new TestTypeProvider(
            new McpTypeInfo("MyClass", "A class", IsInterface: false, Type: typeof(MyConcreteClass)));

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
        var firstType = json.GetProperty("types").EnumerateArray().First();
        var interfaces = firstType.GetProperty("interfaces");
        Assert.Equal(0, interfaces.GetArrayLength());
    }

    [Fact]
    public async Task WhenNoTypeProviders_ThenReturnsEmptyList()
    {
        // Arrange
        var config = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance
        };
        var factory = new McpToolFactory(() => null!, config);
        var tool = factory.CreateTools().First(t => t.Name == "list_types");

        // Act
        var input = JsonSerializer.SerializeToElement(new { });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        Assert.Equal(0, json.GetProperty("types").GetArrayLength());
    }

    [Fact]
    public async Task WhenInterfaceHasReadOnlyProperty_ThenIsWritableIsFalse()
    {
        // Arrange
        var typeProvider = new TestTypeProvider(
            new McpTypeInfo("IHasMethod", "With methods", IsInterface: true, Type: typeof(IHasMethod)));

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
        var firstType = json.GetProperty("types").EnumerateArray().First();
        var properties = firstType.GetProperty("properties");
        var readOnlyProp = properties.EnumerateArray()
            .First(p => p.GetProperty("name").GetString() == "ReadOnlyValue");
        Assert.False(readOnlyProp.GetProperty("isWritable").GetBoolean());
    }

    public interface IHasMethod
    {
        string ReadOnlyValue { get; }
        string DoSomething(string input);
    }

    public interface INoMethods
    {
        string Name { get; set; }
    }

    public class MyConcreteClass : INoMethods
    {
        public string Name { get; set; } = "";
    }

    private class TestTypeProvider : IMcpTypeProvider
    {
        private readonly McpTypeInfo[] _types;
        public TestTypeProvider(params McpTypeInfo[] types) => _types = types;
        public IEnumerable<McpTypeInfo> GetTypes() => _types;
    }
}
