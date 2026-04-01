using System.Text.Json;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Mcp.Tools;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Mcp.Tests.Tools;

public class ListTypesToolTests
{
    [Fact]
    public async Task WhenListingInterfaceType_ThenIncludesProperties()
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
    public async Task WhenListingConcreteType_ThenIncludesKnownInterfaces()
    {
        // Arrange
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

        // Assert
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
    public async Task WhenKindIsInterfaces_ThenFiltersToInterfacesOnly()
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
    public async Task WhenKindIsConcrete_ThenFiltersToConcreteOnly()
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
    public async Task WhenKindIsAll_ThenReturnsEverything()
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
    public async Task WhenTypeSearchByName_ThenFiltersByName()
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
        var input = JsonSerializer.SerializeToElement(new { type = "Motor" });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        var types = json.GetProperty("types");
        Assert.Single(types.EnumerateArray().ToArray());
        Assert.Equal("TestMotor", types.EnumerateArray().First().GetProperty("name").GetString());
    }

    [Fact]
    public async Task WhenTypeSearchByName_ThenIsCaseInsensitive()
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
        var input = JsonSerializer.SerializeToElement(new { type = "motor" });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        var types = json.GetProperty("types");
        Assert.Single(types.EnumerateArray().ToArray());
        Assert.Equal("TestMotor", types.EnumerateArray().First().GetProperty("name").GetString());
    }

    [Fact]
    public async Task WhenKindAndTypeCombined_ThenBothFiltersApply()
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

        // Act
        var input = JsonSerializer.SerializeToElement(new { kind = "interfaces", type = "Motor" });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        var types = json.GetProperty("types");
        Assert.Single(types.EnumerateArray().ToArray());
        Assert.Equal("IMotorInterface", types.EnumerateArray().First().GetProperty("name").GetString());
    }

    [Fact]
    public async Task WhenKindIsConcrete_ThenStillListsAllKnownInterfaces()
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
