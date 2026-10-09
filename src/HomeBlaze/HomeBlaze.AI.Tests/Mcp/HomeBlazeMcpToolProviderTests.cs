using System.Text.Json;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.AI.Mcp;
using HomeBlaze.Services.Lifecycle;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Mcp;
using Namotion.Interceptor.Mcp.Tools;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;
using Xunit;

namespace HomeBlaze.AI.Tests.Mcp;

public class HomeBlazeMcpToolProviderTests
{
    [Fact]
    public async Task WhenListMethods_ThenReturnsMethodMetadata()
    {
        // Arrange
        var (room, config, factory) = CreateTestSetup(isReadOnly: false);

        // Add a method property to the room
        var registered = room.TryGetRegisteredSubject()!;
        registered.AddProperty<MethodMetadata>("TurnOn", _ => new MethodMetadata(_ => "done")
        {
            Kind = MethodKind.Operation,
            Title = "Turn On",
            PropertyName = "TurnOn",
            Parameters = []
        });

        var tool = factory.CreateTools().First(t => t.Name == "list_methods");

        // Act
        var input = JsonSerializer.SerializeToElement(new { path = "" });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        Assert.True(json.TryGetProperty("methods", out var methods));
        var methodArray = methods.EnumerateArray().ToArray();
        Assert.Contains(methodArray, m => m.GetProperty("name").GetString() == "TurnOn");
    }

    [Fact]
    public async Task WhenListMethodsWithDescription_ThenReturnsDescription()
    {
        // Arrange
        var (room, _, factory) = CreateTestSetup(isReadOnly: false);
        room.TryGetRegisteredSubject()!.AddProperty<MethodMetadata>("TurnOn", _ => new MethodMetadata(_ => null)
        {
            Kind = MethodKind.Operation,
            Title = "Turn On",
            Description = "Turns the device on.",
            PropertyName = "TurnOn"
        });

        // Act
        var method = await ListMethodAsync(factory, "TurnOn");

        // Assert
        Assert.Equal("Turns the device on.", method.GetProperty("description").GetString());
    }

    [Fact]
    public async Task WhenListMethodsWithPercentParameter_ThenDescribesFraction()
    {
        // Arrange
        var (room, _, factory) = CreateTestSetup(isReadOnly: false);
        room.TryGetRegisteredSubject()!.AddProperty<MethodMetadata>("SetVolume", _ => new MethodMetadata(_ => null)
        {
            Kind = MethodKind.Operation,
            PropertyName = "SetVolume",
            Parameters =
            [
                new MethodParameter { Name = "volume", Type = typeof(decimal), Unit = StateUnit.Percent },
                new MethodParameter { Name = "cancellationToken", Type = typeof(CancellationToken), IsRuntimeProvided = true }
            ]
        });

        // Act
        var method = await ListMethodAsync(factory, "SetVolume");

        // Assert
        var parameter = Assert.Single(method.GetProperty("parameters").EnumerateArray());
        Assert.Equal("volume", parameter.GetProperty("name").GetString());
        Assert.Equal("number", parameter.GetProperty("type").GetString());
        Assert.Equal("fraction where 1 = 100% (0.2 = 20%)", parameter.GetProperty("description").GetString());
    }

    [Fact]
    public async Task WhenListMethodsWithOtherUnitParameter_ThenNamesTheUnit()
    {
        // Arrange
        var (room, _, factory) = CreateTestSetup(isReadOnly: false);
        room.TryGetRegisteredSubject()!.AddProperty<MethodMetadata>("SetTarget", _ => new MethodMetadata(_ => null)
        {
            Kind = MethodKind.Operation,
            PropertyName = "SetTarget",
            Parameters =
            [
                new MethodParameter { Name = "temperature", Type = typeof(decimal), Unit = StateUnit.DegreeCelsius },
                new MethodParameter { Name = "offset", Type = typeof(decimal), Unit = StateUnit.Default }
            ]
        });

        // Act
        var method = await ListMethodAsync(factory, "SetTarget");

        // Assert
        var parameters = method.GetProperty("parameters").EnumerateArray().ToArray();
        Assert.Equal("unit: DegreeCelsius", parameters[0].GetProperty("description").GetString());
        Assert.False(parameters[1].TryGetProperty("description", out _));
    }

    [Fact]
    public async Task WhenListMethodsWithTimeSpanEnumAndNullableParameters_ThenDescribesFormatValuesAndNullability()
    {
        // Arrange
        var (room, _, factory) = CreateTestSetup(isReadOnly: false);
        room.TryGetRegisteredSubject()!.AddProperty<MethodMetadata>("Configure", _ => new MethodMetadata(_ => null)
        {
            Kind = MethodKind.Operation,
            PropertyName = "Configure",
            Parameters =
            [
                new MethodParameter { Name = "position", Type = typeof(TimeSpan) },
                new MethodParameter { Name = "mode", Type = typeof(TestMode) },
                new MethodParameter { Name = "title", Type = typeof(string), IsNullable = true }
            ]
        });

        // Act
        var method = await ListMethodAsync(factory, "Configure");

        // Assert
        var parameters = method.GetProperty("parameters").EnumerateArray().ToArray();
        Assert.Equal("string", parameters[0].GetProperty("type").GetString());
        Assert.Equal("[d.]hh:mm:ss[.fffffff]", parameters[0].GetProperty("format").GetString());
        Assert.Equal(["Off", "All", "One"], parameters[1].GetProperty("enum").EnumerateArray().Select(value => value.GetString()));
        Assert.True(parameters[2].GetProperty("nullable").GetBoolean());
    }

    [Fact]
    public async Task WhenInvokeMethodWithTimeSpanArgument_ThenConvertsDocumentedFormat()
    {
        // Arrange
        var (room, _, factory) = CreateTestSetup(isReadOnly: false);
        object? capturedPosition = null;
        room.TryGetRegisteredSubject()!.AddProperty<MethodMetadata>("Seek", _ => new MethodMetadata(arguments =>
        {
            capturedPosition = arguments?[0];
            return null;
        })
        {
            Kind = MethodKind.Operation,
            PropertyName = "Seek",
            Parameters = [new MethodParameter { Name = "position", Type = typeof(TimeSpan) }]
        });

        var tool = factory.CreateTools().First(t => t.Name == "invoke_method");

        // Act
        var input = JsonSerializer.SerializeToElement(new { path = "", method = "Seek", parameters = new { position = "00:01:30" } });
        var result = await tool.Handler(input, CancellationToken.None);

        // Assert
        Assert.True(JsonSerializer.SerializeToElement(result).GetProperty("success").GetBoolean());
        Assert.Equal(TimeSpan.FromSeconds(90), capturedPosition);
    }

    [Fact]
    public async Task WhenInvokeMethodWithEnumName_ThenConvertsToEnumValue()
    {
        // Arrange
        var (room, _, factory) = CreateTestSetup(isReadOnly: false);
        object? capturedMode = null;
        room.TryGetRegisteredSubject()!.AddProperty<MethodMetadata>("SetMode", _ => new MethodMetadata(arguments =>
        {
            capturedMode = arguments?[0];
            return null;
        })
        {
            Kind = MethodKind.Operation,
            PropertyName = "SetMode",
            Parameters = [new MethodParameter { Name = "mode", Type = typeof(TestMode) }]
        });

        var tool = factory.CreateTools().First(t => t.Name == "invoke_method");

        // Act
        var input = JsonSerializer.SerializeToElement(new { path = "", method = "SetMode", parameters = new { mode = "All" } });
        var result = await tool.Handler(input, CancellationToken.None);

        // Assert
        Assert.True(JsonSerializer.SerializeToElement(result).GetProperty("success").GetBoolean());
        Assert.Equal(TestMode.All, capturedMode);
    }

    [Fact]
    public async Task WhenInvokeQueryMethodInReadOnlyMode_ThenAllowed()
    {
        // Arrange
        var (room, config, factory) = CreateTestSetup(isReadOnly: true);

        var registered = room.TryGetRegisteredSubject()!;
        registered.AddProperty<MethodMetadata>("GetStatus", _ => new MethodMetadata(_ => "OK")
        {
            Kind = MethodKind.Query,
            Title = "Get Status",
            PropertyName = "GetStatus",
            ResultType = typeof(string),
            Parameters = []
        });

        var tool = factory.CreateTools().First(t => t.Name == "invoke_method");

        // Act
        var input = JsonSerializer.SerializeToElement(new { path = "", method = "GetStatus" });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        Assert.True(json.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task WhenInvokeOperationMethodInReadOnlyMode_ThenBlocked()
    {
        // Arrange
        var (room, config, factory) = CreateTestSetup(isReadOnly: true);

        var registered = room.TryGetRegisteredSubject()!;
        registered.AddProperty<MethodMetadata>("TurnOn", _ => new MethodMetadata(_ => "done")
        {
            Kind = MethodKind.Operation,
            Title = "Turn On",
            PropertyName = "TurnOn",
            Parameters = []
        });

        var tool = factory.CreateTools().First(t => t.Name == "invoke_method");

        // Act
        var input = JsonSerializer.SerializeToElement(new { path = "", method = "TurnOn" });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        Assert.True(json.TryGetProperty("error", out var error));
        Assert.Contains("read-only", error.GetString());
    }

    [Fact]
    public async Task WhenInvokeNonExistentMethod_ThenReturnsError()
    {
        // Arrange
        var (room, config, factory) = CreateTestSetup(isReadOnly: false);
        var tool = factory.CreateTools().First(t => t.Name == "invoke_method");

        // Act
        var input = JsonSerializer.SerializeToElement(new { path = "", method = "DoesNotExist" });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        Assert.True(json.TryGetProperty("error", out var error));
        Assert.Contains("not found", error.GetString());
    }

    [Fact]
    public async Task WhenInvokeOnInvalidPath_ThenReturnsError()
    {
        // Arrange
        var (room, config, factory) = CreateTestSetup(isReadOnly: false);
        var tool = factory.CreateTools().First(t => t.Name == "invoke_method");

        // Act
        var input = JsonSerializer.SerializeToElement(new { path = "NonExistent", method = "DoSomething" });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        Assert.True(json.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task WhenInvokeMethodWithParameters_ThenParametersArePassedCorrectly()
    {
        // Arrange
        var (room, config, factory) = CreateTestSetup(isReadOnly: false);

        string? capturedInput = null;
        var registered = room.TryGetRegisteredSubject()!;
        registered.AddProperty<MethodMetadata>("SetName", _ => new MethodMetadata(args =>
        {
            capturedInput = args?[0] as string;
            return null;
        })
        {
            Kind = MethodKind.Operation,
            Title = "Set Name",
            PropertyName = "SetName",
            Parameters =
            [
                new MethodParameter { Name = "name", Type = typeof(string) }
            ]
        });

        var tool = factory.CreateTools().First(t => t.Name == "invoke_method");

        // Act
        var input = JsonSerializer.SerializeToElement(new
        {
            path = "",
            method = "SetName",
            parameters = new { name = "NewName" }
        });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        Assert.True(json.GetProperty("success").GetBoolean());
        Assert.Equal("NewName", capturedInput);
    }

    [Fact]
    public async Task WhenMethodThrows_ThenReturnsGenericError()
    {
        // Arrange
        var (room, config, factory) = CreateTestSetup(isReadOnly: false);

        var registered = room.TryGetRegisteredSubject()!;
        registered.AddProperty<MethodMetadata>("FailMethod", _ => new MethodMetadata(_ =>
            throw new InvalidOperationException("Internal failure"))
        {
            Kind = MethodKind.Operation,
            Title = "Fail",
            PropertyName = "FailMethod",
            Parameters = []
        });

        var tool = factory.CreateTools().First(t => t.Name == "invoke_method");

        // Act
        var input = JsonSerializer.SerializeToElement(new { path = "", method = "FailMethod" });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert — should return generic error, not expose internal details
        Assert.True(json.TryGetProperty("error", out var error));
        Assert.Contains("failed", error.GetString());
    }

    internal static (TestThing room, McpServerConfiguration config, McpToolFactory factory) CreateTestSetup(bool isReadOnly)
    {
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithService<IPropertyLifecycleHandler>(
                () => new PropertyAttributeInitializer(),
                handler => handler is PropertyAttributeInitializer);

        var room = new TestThing(context) { Name = "Test Room", Temperature = 21.5m };

        var pathProvider = new StateAttributePathProvider();
        var toolProvider = new HomeBlazeMcpToolProvider(
            () => room, pathProvider,
            new EmptyServiceProvider(),
            NullLogger<HomeBlazeMcpToolProvider>.Instance,
            isReadOnly);

        var config = new McpServerConfiguration
        {
            PathProvider = pathProvider,
            IsReadOnly = isReadOnly,
            ToolProviders = { toolProvider }
        };
        var factory = new McpToolFactory(room, config);
        return (room, config, factory);
    }

    private static async Task<JsonElement> ListMethodAsync(McpToolFactory factory, string methodName)
    {
        var tool = factory.CreateTools().First(t => t.Name == "list_methods");
        var result = await tool.Handler(JsonSerializer.SerializeToElement(new { path = "" }), CancellationToken.None);
        return JsonSerializer.SerializeToElement(result).GetProperty("methods").EnumerateArray()
            .First(method => method.GetProperty("name").GetString() == methodName);
    }

    public enum TestMode
    {
        Off,
        All,
        One
    }

    private class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
