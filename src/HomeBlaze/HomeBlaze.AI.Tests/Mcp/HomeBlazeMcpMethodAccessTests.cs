using System.Text.Json;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.AI.Mcp;
using HomeBlaze.Authorization;
using Namotion.Interceptor.Authorization;
using Namotion.Interceptor.Mcp;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Mcp.Tools;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Xunit;

namespace HomeBlaze.AI.Tests.Mcp;

public class HomeBlazeMcpMethodAccessTests
{
    private const string OperationName = "Restart";
    private const string QueryName = "GetStatus";

    [Fact]
    public async Task WhenAReaderInvokesAnOperation_ThenItIsRefusedWithoutInvoking()
    {
        // Arrange
        var graph = CreateGraph(TestAccessPolicies.Create(BuiltInRoles.ReaderName));

        // Act
        var result = await CallAsync(graph.Tools[HomeBlazeMcpToolProvider.InvokeOperationToolName], new { path = "", method = OperationName });

        // Assert
        Assert.Equal("Operation 'Restart' is not permitted. The access policy does not allow it.", result.GetProperty("error").GetString());
        Assert.Equal(0, graph.Invocations[OperationName]);
    }

    [Fact]
    public async Task WhenAReaderInvokesAQuery_ThenItIsInvoked()
    {
        // Arrange
        var graph = CreateGraph(TestAccessPolicies.Create(BuiltInRoles.ReaderName));

        // Act
        var result = await CallAsync(graph.Tools[HomeBlazeMcpToolProvider.InvokeQueryToolName], new { path = "", method = QueryName });

        // Assert
        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Equal(1, graph.Invocations[QueryName]);
    }

    [Fact]
    public async Task WhenAnOperatorInvokesAnOperation_ThenItIsInvoked()
    {
        // Arrange
        var graph = CreateGraph(TestAccessPolicies.Operator);

        // Act
        var result = await CallAsync(graph.Tools[HomeBlazeMcpToolProvider.InvokeOperationToolName], new { path = "", method = OperationName });

        // Assert
        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Equal(1, graph.Invocations[OperationName]);
    }

    [Fact]
    public async Task WhenARoleDeniesExecuteOnAQuery_ThenItIsRefusedWithTheQueryWordingWithoutInvoking()
    {
        // Arrange
        var role = new AuthorizationRole
        {
            Name = "NoStatus",
            Includes = [BuiltInRoles.ReaderName],
            Deny = [new PermissionRule { Members = [QueryName], Actions = [AuthorizationActions.Execute] }]
        };
        var graph = CreateGraph(TestAccessPolicies.Create(role.Name, role));

        // Act
        var result = await CallAsync(graph.Tools[HomeBlazeMcpToolProvider.InvokeQueryToolName], new { path = "", method = QueryName });

        // Assert
        Assert.Equal("Query 'GetStatus' is not permitted. The access policy does not allow it.", result.GetProperty("error").GetString());
        Assert.Equal(0, graph.Invocations[QueryName]);
    }

    [Fact]
    public async Task WhenARoleDeniesReadOnAMethod_ThenListMethodsAndMethodsLeaveItOutAndInvokingItIsNotFound()
    {
        // Arrange
        var role = new AuthorizationRole
        {
            Name = "NoRestart",
            Includes = [BuiltInRoles.OperatorName],
            Deny = [new PermissionRule { Members = [OperationName], Actions = [PermissionAction.Read] }]
        };
        var graph = CreateGraph(TestAccessPolicies.Create(role.Name, role));

        // Act
        var listed = await CallAsync(graph.Tools["list_methods"], new { path = "" });
        var enrichments = graph.Configuration.SubjectEnrichers
            .SelectMany(enricher => enricher.GetSubjectEnrichments(graph.Room.TryGetRegisteredSubject()!))
            .ToDictionary(entry => entry.Key, entry => entry.Value);
        var invoked = await CallAsync(graph.Tools[HomeBlazeMcpToolProvider.InvokeOperationToolName], new { path = "", method = OperationName });

        // Assert
        Assert.Equal([QueryName], listed.GetProperty("methods").EnumerateArray().Select(method => method.GetProperty("name").GetString()));
        Assert.Equal([QueryName], Assert.IsType<string[]>(enrichments["$methods"]));
        Assert.Equal("Method not found: Restart", invoked.GetProperty("error").GetString());
        Assert.Equal(0, graph.Invocations[OperationName]);
    }

    [Theory]
    [InlineData("list_methods")]
    [InlineData(HomeBlazeMcpToolProvider.InvokeQueryToolName)]
    [InlineData(HomeBlazeMcpToolProvider.InvokeOperationToolName)]
    public async Task WhenASubjectPathIsHidden_ThenTheMethodToolsAnswerNotFound(string toolName)
    {
        // Arrange
        var role = new AuthorizationRole
        {
            Name = "NoChild",
            Includes = [BuiltInRoles.OperatorName],
            Deny = [new PermissionRule { Members = [nameof(TestThing.Child)], Actions = [PermissionAction.Read] }]
        };
        var graph = CreateGraph(TestAccessPolicies.Create(role.Name, role));
        var method = toolName == HomeBlazeMcpToolProvider.InvokeQueryToolName ? QueryName : OperationName;

        // Act
        var result = await CallAsync(graph.Tools[toolName], new { path = nameof(TestThing.Child), method });

        // Assert
        Assert.Equal("Subject not found.", result.GetProperty("error").GetString());
        Assert.Equal(0, graph.ChildInvocations);
    }

    private static Graph CreateGraph(IMcpAccessPolicy accessPolicy)
    {
        var (room, _) = HomeBlazeMcpToolProviderTests.CreateTestSetup(isReadOnly: false);
        var invocations = new Dictionary<string, int> { [OperationName] = 0, [QueryName] = 0 };
        AddMethod(room.TryGetRegisteredSubject()!, OperationName, MethodKind.Operation, () => invocations[OperationName]++);
        AddMethod(room.TryGetRegisteredSubject()!, QueryName, MethodKind.Query, () => invocations[QueryName]++);

        var graph = new Graph(room, invocations);
        room.Child = new TestChildThing { ChildName = "Child" };
        AddMethod(room.Child.TryGetRegisteredSubject()!, OperationName, MethodKind.Operation, () => graph.ChildInvocations++);
        AddMethod(room.Child.TryGetRegisteredSubject()!, QueryName, MethodKind.Query, () => graph.ChildInvocations++);

        graph.Configuration = HomeBlazeMcpConfigurationFactory.Create(
            HomeBlazeMcpConfigurationFactoryTests.CreateServices(), () => room, isReadOnly: false, accessPolicy);
        graph.Tools = new McpToolFactory(room, graph.Configuration).CreateTools().ToDictionary(tool => tool.Name);
        return graph;
    }

    private static void AddMethod(RegisteredSubject subject, string name, MethodKind kind, Action invoke) =>
        subject.AddProperty<MethodMetadata>(name, _ => new MethodMetadata(_ =>
        {
            invoke();
            return null;
        })
        {
            Kind = kind,
            Title = name,
            PropertyName = name
        });

    private static async Task<JsonElement> CallAsync(McpToolInfo tool, object input) =>
        JsonSerializer.SerializeToElement(await tool.Handler(JsonSerializer.SerializeToElement(input), CancellationToken.None));

    private sealed class Graph(TestThing room, Dictionary<string, int> invocations)
    {
        public TestThing Room { get; } = room;

        public Dictionary<string, int> Invocations { get; } = invocations;

        public int ChildInvocations { get; set; }

        public McpServerConfiguration Configuration { get; set; } = null!;

        public Dictionary<string, McpToolInfo> Tools { get; set; } = null!;
    }
}
