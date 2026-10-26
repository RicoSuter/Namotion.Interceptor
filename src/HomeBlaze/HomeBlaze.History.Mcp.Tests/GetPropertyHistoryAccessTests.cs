using System.Text.Json;
using HomeBlaze.AI.Mcp;
using HomeBlaze.Authorization;
using HomeBlaze.Services.Lifecycle;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Authorization;
using Namotion.Interceptor.Mcp;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;
using Xunit;

namespace HomeBlaze.History.Mcp.Tests;

public class GetPropertyHistoryAccessTests
{
    [Fact]
    public async Task WhenAReaderAsksForTheHistoryOfState_ThenItIsReturned()
    {
        // Arrange
        var graph = CreateGraph(CreatePolicy(BuiltInRoles.ReaderName));

        // Act
        var json = await InvokeAsync(graph, "/Power");

        // Assert
        var entry = json.GetProperty("series").GetProperty("/Power");
        Assert.False(entry.TryGetProperty("error", out _));
        Assert.Equal(["/Power"], graph.Store.QueriedPaths);
    }

    [Fact]
    public async Task WhenARoleDoesNotAllowHistory_ThenThePathIsRefusedWithoutAQuery()
    {
        // Arrange
        var role = new AuthorizationRole
        {
            Name = "NoHistory",
            Includes = [BuiltInRoles.ReaderName],
            Deny = [new PermissionRule { Tags = [MemberTags.State], Actions = [AuthorizationActions.History] }]
        };
        var graph = CreateGraph(CreatePolicy(role.Name, role));

        // Act
        var json = await InvokeAsync(graph, "/Power");

        // Assert
        Assert.Equal(
            "History is not available: /Power. The access policy does not allow it.",
            json.GetProperty("series").GetProperty("/Power").GetProperty("error").GetString());
        Assert.Empty(graph.Store.QueriedPaths);
    }

    [Fact]
    public async Task WhenThePathDoesNotResolve_ThenItIsNotFoundWithoutAQuery()
    {
        // Arrange
        var graph = CreateGraph(CreatePolicy(BuiltInRoles.ReaderName));

        // Act
        var json = await InvokeAsync(graph, "/Missing/Value");

        // Assert
        Assert.Equal("Path not found: /Missing/Value", json.GetProperty("series").GetProperty("/Missing/Value").GetProperty("error").GetString());
        Assert.Empty(graph.Store.QueriedPaths);
    }

    [Fact]
    public async Task WhenThePropertyIsSecret_ThenTheSystemRuleHidesItWithoutAQuery()
    {
        // Arrange
        var graph = CreateGraph(CreatePolicy(BuiltInRoles.EverythingName));

        // Act
        var json = await InvokeAsync(graph, "/Password");

        // Assert
        Assert.Equal("Path not found: /Password", json.GetProperty("series").GetProperty("/Password").GetProperty("error").GetString());
        Assert.Empty(graph.Store.QueriedPaths);
    }

    [Fact]
    public async Task WhenABranchIsHidden_ThenAPathBelowItIsNotFoundWithoutAQuery()
    {
        // Arrange
        var role = new AuthorizationRole
        {
            Name = "NoChild",
            Includes = [BuiltInRoles.ReaderName],
            Deny = [new PermissionRule { Members = [nameof(HistoryToolTestSubject.Child)], Actions = [PermissionAction.Read] }]
        };
        var graph = CreateGraph(CreatePolicy(role.Name, role));

        // Act
        var json = await InvokeAsync(graph, "/Child/Power");

        // Assert
        Assert.Equal("Path not found: /Child/Power", json.GetProperty("series").GetProperty("/Child/Power").GetProperty("error").GetString());
        Assert.Empty(graph.Store.QueriedPaths);
    }

    [Fact]
    public async Task WhenTheConfigurationHasNoAccessPolicy_ThenAnUnresolvedPathIsQueried()
    {
        // Arrange
        var graph = CreateGraph(accessPolicy: null);

        // Act
        var json = await InvokeAsync(graph, "/Missing/Value");

        // Assert
        Assert.False(json.GetProperty("series").GetProperty("/Missing/Value").TryGetProperty("error", out _));
        Assert.Equal(["/Missing/Value"], graph.Store.QueriedPaths);
    }

    private static Graph CreateGraph(IMcpAccessPolicy? accessPolicy)
    {
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithService<IPropertyLifecycleHandler>(
                () => new PropertyAttributeInitializer(),
                handler => handler is PropertyAttributeInitializer);

        var root = new HistoryToolTestSubject(context) { Name = "Room", Password = "s3cr3t" };
        root.Child = new HistoryToolTestSubject { Name = "Child" };
        var store = new TestHistoryStore(context);
        var configuration = new McpServerConfiguration
        {
            PathProvider = new StateAttributePathProvider(),
            AccessPolicy = accessPolicy
        };

        var provider = new HistoryMcpToolProvider(() => root, configuration, NullLogger<HistoryMcpToolProvider>.Instance);
        return new Graph(provider.GetTools().Single(), store);
    }

    private static AuthorizationMcpAccessPolicy CreatePolicy(string roleName, params AuthorizationRole[] customRoles) =>
        new(AuthorizationPolicy.Compile(
            [.. BuiltInRoles.All, .. customRoles],
            [roleName],
            SystemRules.Rules,
            AuthorizationActions.All,
            new HomeBlazeMemberTagProvider(),
            new StateAttributePathProvider()));

    private static async Task<JsonElement> InvokeAsync(Graph graph, string path)
    {
        var input = JsonSerializer.SerializeToElement(new { paths = new[] { path }, from = "2026-06-24T00:00:00Z", to = "2026-06-24T01:00:00Z" });
        return JsonSerializer.SerializeToElement(await graph.Tool.Handler(input, CancellationToken.None));
    }

    private sealed record Graph(McpToolInfo Tool, TestHistoryStore Store);
}
