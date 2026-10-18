using System.Text.Json;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Messaging;
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
        var (room, factory) = CreateTestSetup(isReadOnly: false);

        AddMethod(room, new MethodMetadata(_ => "done")
        {
            Kind = MethodKind.Operation,
            Title = "Turn On",
            PropertyName = "TurnOn",
            Parameters = []
        });

        // Act
        var method = await ListMethodAsync(factory, "TurnOn");

        // Assert
        Assert.Equal("Turn On", method.GetProperty("title").GetString());
    }

    [Fact]
    public async Task WhenListMethodsWithDescription_ThenReturnsDescription()
    {
        // Arrange
        var (room, factory) = CreateTestSetup(isReadOnly: false);
        AddMethod(room, new MethodMetadata(_ => null)
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
        var (room, factory) = CreateTestSetup(isReadOnly: false);
        AddMethod(room, new MethodMetadata(_ => null)
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
        var (room, factory) = CreateTestSetup(isReadOnly: false);
        AddMethod(room, new MethodMetadata(_ => null)
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
        var (room, factory) = CreateTestSetup(isReadOnly: false);
        AddMethod(room, new MethodMetadata(_ => null)
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
        Assert.Equal("hh:mm:ss", parameters[0].GetProperty("format").GetString());
        Assert.Equal(["Off", "All", "One"], parameters[1].GetProperty("enum").EnumerateArray().Select(value => value.GetString()));
        Assert.True(parameters[2].GetProperty("nullable").GetBoolean());
    }

    [Fact]
    public async Task WhenInvokeOperationWithTimeSpanArgument_ThenConvertsDocumentedFormat()
    {
        // Arrange
        var (room, factory) = CreateTestSetup(isReadOnly: false);
        object? capturedPosition = null;
        AddMethod(room, new MethodMetadata(arguments =>
        {
            capturedPosition = arguments?[0];
            return null;
        })
        {
            Kind = MethodKind.Operation,
            PropertyName = "Seek",
            Parameters = [new MethodParameter { Name = "position", Type = typeof(TimeSpan) }]
        });

        var tool = factory.CreateTools().First(t => t.Name == "invoke_operation");

        // Act
        var input = JsonSerializer.SerializeToElement(new { path = "", method = "Seek", parameters = new { position = "00:01:30" } });
        var result = await tool.Handler(input, CancellationToken.None);

        // Assert
        Assert.True(JsonSerializer.SerializeToElement(result).GetProperty("success").GetBoolean());
        Assert.Equal(TimeSpan.FromSeconds(90), capturedPosition);
    }

    [Fact]
    public async Task WhenInvokeOperationWithEnumName_ThenConvertsToEnumValue()
    {
        // Arrange
        var (room, factory) = CreateTestSetup(isReadOnly: false);
        object? capturedMode = null;
        AddMethod(room, new MethodMetadata(arguments =>
        {
            capturedMode = arguments?[0];
            return null;
        })
        {
            Kind = MethodKind.Operation,
            PropertyName = "SetMode",
            Parameters = [new MethodParameter { Name = "mode", Type = typeof(TestMode) }]
        });

        var tool = factory.CreateTools().First(t => t.Name == "invoke_operation");

        // Act
        var input = JsonSerializer.SerializeToElement(new { path = "", method = "SetMode", parameters = new { mode = "All" } });
        var result = await tool.Handler(input, CancellationToken.None);

        // Assert
        Assert.True(JsonSerializer.SerializeToElement(result).GetProperty("success").GetBoolean());
        Assert.Equal(TestMode.All, capturedMode);
    }

    [Theory]
    [InlineData("""{"level": 0.3}""", "Unknown parameter 'level'.")]
    [InlineData("""{}""", "Missing parameter 'volume'.")]
    [InlineData(null, "Missing parameter 'volume'.")]
    [InlineData("""[0.3]""", "'parameters' must be an object.")]
    public async Task WhenInvokeOperationArgumentIsMissingOrMisspelled_ThenReturnsErrorNamingTheParametersWithoutInvoking(string? parameters, string expectedError)
    {
        // Arrange
        var (room, factory) = CreateTestSetup(isReadOnly: false);
        var isInvoked = false;
        AddMethod(room, new MethodMetadata(_ =>
        {
            isInvoked = true;
            return null;
        })
        {
            Kind = MethodKind.Operation,
            PropertyName = "SetVolume",
            Parameters =
            [
                new MethodParameter { Name = "volume", Type = typeof(decimal) },
                new MethodParameter { Name = "title", Type = typeof(string), IsNullable = true },
                new MethodParameter { Name = "cancellationToken", Type = typeof(CancellationToken), IsRuntimeProvided = true }
            ]
        });

        // Act
        var error = await InvokeForErrorAsync(factory, "SetVolume", parameters);

        // Assert
        Assert.Equal($"{expectedError} Expected parameters: volume, title.", error);
        Assert.False(isInvoked);
    }

    [Fact]
    public async Task WhenInvokeOperationWithoutNullableArgument_ThenItIsPassedAsNull()
    {
        // Arrange
        var (room, factory) = CreateTestSetup(isReadOnly: false);
        object?[]? capturedArguments = null;
        AddMethod(room, new MethodMetadata(arguments =>
        {
            capturedArguments = arguments;
            return null;
        })
        {
            Kind = MethodKind.Operation,
            PropertyName = "PlayStream",
            Parameters =
            [
                new MethodParameter { Name = "uri", Type = typeof(string) },
                new MethodParameter { Name = "title", Type = typeof(string), IsNullable = true }
            ]
        });

        var tool = factory.CreateTools().First(t => t.Name == "invoke_operation");

        // Act
        var input = JsonSerializer.SerializeToElement(new { path = "", method = "PlayStream", parameters = new { uri = "http://example.com/live" } });
        var result = await tool.Handler(input, CancellationToken.None);

        // Assert
        Assert.True(JsonSerializer.SerializeToElement(result).GetProperty("success").GetBoolean());
        Assert.Equal(["http://example.com/live", null], capturedArguments!);
    }

    [Theory]
    [InlineData("""{"mode": 42}""")]
    [InlineData("""{"mode": "Off, One"}""")]
    [InlineData("""{"mode": "Sometimes"}""")]
    public async Task WhenInvokeOperationEnumArgumentIsUndefined_ThenReturnsErrorListingTheValuesWithoutInvoking(string parameters)
    {
        // Arrange
        var (room, factory) = CreateTestSetup(isReadOnly: false);
        var isInvoked = false;
        AddMethod(room, new MethodMetadata(_ =>
        {
            isInvoked = true;
            return null;
        })
        {
            Kind = MethodKind.Operation,
            PropertyName = "SetMode",
            Parameters = [new MethodParameter { Name = "mode", Type = typeof(TestMode) }]
        });

        // Act
        var error = await InvokeForErrorAsync(factory, "SetMode", parameters);

        // Assert
        Assert.Contains("'mode'", error);
        Assert.Contains("Off, All, One", error);
        Assert.False(isInvoked);
    }

    [Fact]
    public async Task WhenMethodRejectsAnArgument_ThenTheArgumentErrorReachesTheCaller()
    {
        // Arrange
        var (room, factory) = CreateTestSetup(isReadOnly: false);
        static object? PlayFavorite(string? title) =>
            throw new ArgumentException($"Unknown favorite '{title}'. Known favorites: Rock, Pop.", nameof(title));

        AddMethod(room, new MethodMetadata(arguments =>
            PlayFavorite((string?)arguments![0]))
        {
            Kind = MethodKind.Operation,
            PropertyName = "PlayFavorite",
            Parameters = [new MethodParameter { Name = "title", Type = typeof(string) }]
        });

        // Act
        var error = await InvokeForErrorAsync(factory, "PlayFavorite", """{"title": "Jazz"}""");

        // Assert
        Assert.StartsWith("Unknown favorite 'Jazz'. Known favorites: Rock, Pop.", error);
    }

    [Fact]
    public async Task WhenInvokeQueryCallsAQueryInReadOnlyMode_ThenItIsInvoked()
    {
        // Arrange
        var (room, factory) = CreateTestSetup(isReadOnly: true);
        AddMethod(room, new MethodMetadata(_ => "OK")
        {
            Kind = MethodKind.Query,
            Title = "Get Status",
            PropertyName = "GetStatus",
            ResultType = typeof(string)
        });

        var tool = factory.CreateTools().First(t => t.Name == "invoke_query");

        // Act
        var input = JsonSerializer.SerializeToElement(new { path = "", method = "GetStatus" });
        var json = JsonSerializer.SerializeToElement(await tool.Handler(input, CancellationToken.None));

        // Assert
        Assert.True(json.GetProperty("success").GetBoolean());
        Assert.Equal("OK", json.GetProperty("result").GetString());
    }

    [Theory]
    [InlineData(false, "'TurnOn' is an operation; call it with invoke_operation.")]
    [InlineData(true, "'TurnOn' is an operation; it can only be called with invoke_operation, which is not available in read-only mode.")]
    public async Task WhenInvokeQueryCallsAnOperation_ThenErrorNamesInvokeOperationWithoutInvoking(bool isReadOnly, string expectedError)
    {
        // Arrange
        var (room, factory) = CreateTestSetup(isReadOnly);
        var isInvoked = false;
        AddMethod(room, new MethodMetadata(_ =>
        {
            isInvoked = true;
            return null;
        })
        {
            Kind = MethodKind.Operation,
            PropertyName = "TurnOn"
        });

        // Act
        var error = await InvokeForErrorAsync(factory, "TurnOn", parameters: null, toolName: "invoke_query");

        // Assert
        Assert.Equal(expectedError, error);
        Assert.False(isInvoked);
    }

    [Fact]
    public async Task WhenInvokeOperationCallsAQuery_ThenErrorNamesInvokeQueryWithoutInvoking()
    {
        // Arrange
        var (room, factory) = CreateTestSetup(isReadOnly: false);
        var isInvoked = false;
        AddMethod(room, new MethodMetadata(_ =>
        {
            isInvoked = true;
            return "OK";
        })
        {
            Kind = MethodKind.Query,
            PropertyName = "GetStatus",
            ResultType = typeof(string)
        });

        // Act
        var error = await InvokeForErrorAsync(factory, "GetStatus", parameters: null, toolName: "invoke_operation");

        // Assert
        Assert.Equal("'GetStatus' is a query; call it with invoke_query.", error);
        Assert.False(isInvoked);
    }

    [Fact]
    public async Task WhenInvokeOperationCallsAnOperationThatRequiresConfirmation_ThenItIsRefusedWithoutInvoking()
    {
        // Arrange
        var (room, factory) = CreateTestSetup(isReadOnly: false);
        var isInvoked = false;
        AddMethod(room, new MethodMetadata(_ =>
        {
            isInvoked = true;
            return null;
        })
        {
            Kind = MethodKind.Operation,
            PropertyName = "Unlock",
            RequiresConfirmation = true,
            Parameters = [new MethodParameter { Name = "code", Type = typeof(string) }]
        });

        // Act
        var error = await InvokeForErrorAsync(factory, "Unlock", parameters: null, toolName: "invoke_operation");

        // Assert
        Assert.Equal("'Unlock' requires confirmation by a person and cannot be called through MCP; run it from the HomeBlaze UI.", error);
        Assert.False(isInvoked);
    }

    [Fact]
    public async Task WhenListMethods_ThenEachMethodReportsWhetherItRequiresConfirmation()
    {
        // Arrange
        var (room, factory) = CreateTestSetup(isReadOnly: false);
        AddMethod(room, new MethodMetadata(_ => null) { Kind = MethodKind.Operation, PropertyName = "Unlock", RequiresConfirmation = true });
        AddMethod(room, new MethodMetadata(_ => null) { Kind = MethodKind.Operation, PropertyName = "Lock" });

        // Act
        var unlock = await ListMethodAsync(factory, "Unlock");
        var lockMethod = await ListMethodAsync(factory, "Lock");

        // Assert
        Assert.True(unlock.GetProperty("requiresConfirmation").GetBoolean());
        Assert.False(lockMethod.GetProperty("requiresConfirmation").GetBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenListingTools_ThenExactlyTheToolsOfTheModeAreOfferedWithInvokeOperationOnlyWhenNotReadOnly(bool isReadOnly)
    {
        // Arrange
        var (_, factory) = CreateTestSetup(isReadOnly);

        // Act
        var toolNames = factory.CreateTools().Select(tool => tool.Name).Order(StringComparer.Ordinal).ToArray();

        // Assert
        string[] expected = isReadOnly
            ? ["browse", "get_property", "invoke_query", "list_methods", "list_types", "search", "set_property"]
            : ["browse", "get_property", "invoke_operation", "invoke_query", "list_methods", "list_types", "search", "set_property"];
        Assert.Equal(expected, toolNames);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenListMethods_ThenEachMethodReportsItsKind(bool isReadOnly)
    {
        // Arrange
        var (room, factory) = CreateTestSetup(isReadOnly);
        AddMethod(room, new MethodMetadata(_ => null) { Kind = MethodKind.Operation, PropertyName = "TurnOn" });
        AddMethod(room, new MethodMetadata(_ => "OK") { Kind = MethodKind.Query, PropertyName = "GetStatus" });

        // Act
        var operation = await ListMethodAsync(factory, "TurnOn");
        var query = await ListMethodAsync(factory, "GetStatus");

        // Assert
        Assert.Equal("operation", operation.GetProperty("kind").GetString());
        Assert.Equal("query", query.GetProperty("kind").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenListingTools_ThenTheMethodToolDescriptionsNameEachOtherOnlyWhereTheToolIsOffered(bool isReadOnly)
    {
        // Arrange
        var (_, factory) = CreateTestSetup(isReadOnly);

        // Act
        var tools = factory.CreateTools().ToDictionary(tool => tool.Name, tool => tool.Description);

        // Assert
        Assert.Contains("invoke_query", tools["list_methods"]);
        Assert.Equal(!isReadOnly, tools["list_methods"].Contains("invoke_operation", StringComparison.Ordinal));
        Assert.All(tools.Where(tool => tool.Key.StartsWith("invoke_", StringComparison.Ordinal)), tool =>
            Assert.Contains("Find methods and their kind with list_methods", tool.Value));
        if (!isReadOnly)
        {
            Assert.Contains("requiresConfirmation", tools["list_methods"]);
            Assert.Contains("Operations that require confirmation by a person cannot be called.", tools["invoke_operation"]);
            Assert.Contains(HomeBlazeMcpInstructions.ChangeCaution, tools["invoke_operation"]);
        }
    }

    [Theory]
    [InlineData("invoke_query", MethodKind.Query)]
    [InlineData("invoke_operation", MethodKind.Operation)]
    public async Task WhenAMethodReturnsALongString_ThenTheResultIsCutWithAMarker(string toolName, MethodKind kind)
    {
        // Arrange
        var (room, factory) = CreateTestSetup(isReadOnly: false);
        var text = new string('a', MethodResultLimit.MaxStringLength + 10);
        AddMethod(room, new MethodMetadata(_ => text)
        {
            Kind = kind,
            PropertyName = "Read",
            ResultType = typeof(string)
        });

        var tool = factory.CreateTools().First(t => t.Name == toolName);

        // Act
        var input = JsonSerializer.SerializeToElement(new { path = "", method = "Read" });
        var json = JsonSerializer.SerializeToElement(await tool.Handler(input, CancellationToken.None));

        // Assert
        var result = json.GetProperty("result").GetString()!;
        Assert.StartsWith(new string('a', MethodResultLimit.MaxStringLength), result);
        Assert.EndsWith("[truncated: 10 characters]", result);
    }

    [Fact]
    public async Task WhenInvokeNonExistentMethod_ThenReturnsError()
    {
        // Arrange
        var (_, factory) = CreateTestSetup(isReadOnly: false);
        var tool = factory.CreateTools().First(t => t.Name == "invoke_operation");

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
        var (_, factory) = CreateTestSetup(isReadOnly: false);
        var tool = factory.CreateTools().First(t => t.Name == "invoke_operation");

        // Act
        var input = JsonSerializer.SerializeToElement(new { path = "NonExistent", method = "DoSomething" });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        Assert.True(json.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task WhenInvokeOperationWithParameters_ThenParametersArePassedCorrectly()
    {
        // Arrange
        var (room, factory) = CreateTestSetup(isReadOnly: false);

        string? capturedInput = null;
        AddMethod(room, new MethodMetadata(args =>
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

        var tool = factory.CreateTools().First(t => t.Name == "invoke_operation");

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
        var (room, factory) = CreateTestSetup(isReadOnly: false);

        AddMethod(room, new MethodMetadata(_ =>
            throw new InvalidOperationException("Internal failure"))
        {
            Kind = MethodKind.Operation,
            Title = "Fail",
            PropertyName = "FailMethod",
            Parameters = []
        });

        var tool = factory.CreateTools().First(t => t.Name == "invoke_operation");

        // Act
        var input = JsonSerializer.SerializeToElement(new { path = "", method = "FailMethod" });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert — should return generic error, not expose internal details
        Assert.True(json.TryGetProperty("error", out var error));
        Assert.Contains("failed", error.GetString());
    }

    [Fact]
    public async Task WhenTheCallIsCancelledDuringTheMethod_ThenTheCancellationPropagates()
    {
        // Arrange
        var (room, factory) = CreateTestSetup(isReadOnly: false);
        using var cancellationTokenSource = new CancellationTokenSource();
        AddMethod(room, new MethodMetadata(_ =>
        {
            cancellationTokenSource.Cancel();
            throw new OperationCanceledException(cancellationTokenSource.Token);
        })
        {
            Kind = MethodKind.Operation,
            PropertyName = "Wait",
            Parameters = []
        });

        var tool = factory.CreateTools().First(t => t.Name == "invoke_operation");
        var input = JsonSerializer.SerializeToElement(new { path = "", method = "Wait" });

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tool.Handler(input, cancellationTokenSource.Token));
    }

    [Fact]
    public async Task WhenAMethodThrowsCancellationWithoutTheCallBeingCancelled_ThenReturnsGenericError()
    {
        // Arrange
        var (room, factory) = CreateTestSetup(isReadOnly: false);
        AddMethod(room, new MethodMetadata(_ => throw new OperationCanceledException())
        {
            Kind = MethodKind.Operation,
            PropertyName = "Wait",
            Parameters = []
        });

        var tool = factory.CreateTools().First(t => t.Name == "invoke_operation");

        // Act
        var input = JsonSerializer.SerializeToElement(new { path = "", method = "Wait" });
        var json = JsonSerializer.SerializeToElement(await tool.Handler(input, CancellationToken.None));

        // Assert
        Assert.Equal("Method invocation failed. Check server logs for details.", json.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("list_methods")]
    [InlineData("invoke_query")]
    public async Task WhenThePathArgumentIsMissing_ThenTheToolNamesIt(string toolName)
    {
        // Arrange
        var (_, factory) = CreateTestSetup(isReadOnly: true);
        var tool = factory.CreateTools().First(t => t.Name == toolName);

        // Act
        var json = JsonSerializer.SerializeToElement(await tool.Handler(
            JsonSerializer.SerializeToElement(new { method = "Read" }), CancellationToken.None));

        // Assert
        Assert.Equal("Missing parameter 'path'.", json.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("list_methods")]
    [InlineData("invoke_query")]
    public async Task WhenThePathArgumentIsNotAString_ThenTheToolSaysItMustBeAString(string toolName)
    {
        // Arrange
        var (_, factory) = CreateTestSetup(isReadOnly: true);
        var tool = factory.CreateTools().First(t => t.Name == toolName);

        // Act
        var json = JsonSerializer.SerializeToElement(await tool.Handler(
            JsonSerializer.SerializeToElement(new { path = 42, method = "Read" }), CancellationToken.None));

        // Assert
        Assert.Equal("Parameter 'path' must be a string.", json.GetProperty("error").GetString());
    }

    internal static (TestThing Room, McpToolFactory Factory) CreateTestSetup(bool isReadOnly)
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
        var invoker = new McpMethodInvoker(
            () => room, pathProvider, new EmptyServiceProvider(), NullLogger.Instance, isReadOnly, operationFilter: null);
        var toolProvider = new HomeBlazeMcpToolProvider(invoker, isReadOnly);

        var configuration = new McpServerConfiguration
        {
            PathProvider = pathProvider,
            IsReadOnly = isReadOnly,
            ToolProviders = { toolProvider }
        };
        return (room, new McpToolFactory(room, configuration));
    }

    [Fact]
    public async Task WhenListMethodsWithNotificationParameter_ThenDescribesItsMembers()
    {
        // Arrange
        var (room, factory) = CreateTestSetup(isReadOnly: false);
        AddMethod(room, new MethodMetadata(_ => null)
        {
            Kind = MethodKind.Operation,
            PropertyName = "SendNotification",
            Parameters = [new MethodParameter { Name = "notification", Type = typeof(Notification) }]
        });

        // Act
        var method = await ListMethodAsync(factory, "SendNotification");

        // Assert
        var notification = method.GetProperty("parameters")[0];
        var members = notification.GetProperty("properties").EnumerateArray().ToArray();
        Assert.Equal(["message", "title", "priority", "url", "urlTitle"], members.Select(member => member.GetProperty("name").GetString()));
        Assert.True(members[0].GetProperty("required").GetBoolean());
        Assert.Equal(["Low", "Normal", "High", "Emergency"], members[2].GetProperty("enum").EnumerateArray().Select(value => value.GetString()));
    }

    [Fact]
    public async Task WhenInvokeOperationWithNotificationArgument_ThenMembersAreBoundAndTheResultIdIsReturned()
    {
        // Arrange
        var (room, factory) = CreateTestSetup(isReadOnly: false);
        object? capturedNotification = null;
        AddMethod(room, new MethodMetadata(arguments =>
        {
            capturedNotification = arguments?[0];
            return Task.FromResult(new NotificationResult { Id = "receipt1" });
        })
        {
            Kind = MethodKind.Operation,
            PropertyName = "SendNotification",
            Parameters = [new MethodParameter { Name = "notification", Type = typeof(Notification) }],
            ResultType = typeof(NotificationResult)
        });

        var tool = factory.CreateTools().First(t => t.Name == "invoke_operation");

        // Act
        var input = JsonSerializer.SerializeToElement(new
        {
            path = "",
            method = "SendNotification",
            parameters = new { notification = new { message = "Door open", priority = "High" } }
        });
        var result = await tool.Handler(input, CancellationToken.None);

        // Assert
        var json = JsonSerializer.SerializeToElement(result);
        Assert.True(json.GetProperty("success").GetBoolean());
        Assert.Equal("receipt1", json.GetProperty("result").GetProperty("Id").GetString());
        var notification = Assert.IsType<Notification>(capturedNotification);
        Assert.Equal("Door open", notification.Message);
        Assert.Equal(NotificationPriority.High, notification.Priority);
        Assert.Null(notification.Title);
    }

    private static void AddMethod(TestThing room, MethodMetadata method) =>
        room.TryGetRegisteredSubject()!.AddProperty<MethodMetadata>(method.PropertyName, _ => method);

    private static async Task<string> InvokeForErrorAsync(McpToolFactory factory, string methodName, string? parameters, string toolName = "invoke_operation")
    {
        var tool = factory.CreateTools().First(t => t.Name == toolName);
        var input = parameters is null
            ? JsonSerializer.SerializeToElement(new { path = "", method = methodName })
            : JsonSerializer.SerializeToElement(new { path = "", method = methodName, parameters = JsonDocument.Parse(parameters).RootElement });
        var result = await tool.Handler(input, CancellationToken.None);
        return JsonSerializer.SerializeToElement(result).GetProperty("error").GetString()!;
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
