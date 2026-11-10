using System.Text.Json;
using HomeBlaze.AI.Mcp;
using HomeBlaze.Authorization;
using HomeBlaze.Automation;
using HomeBlaze.Services;
using HomeBlaze.Services.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Mcp;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Mcp.Tools;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;
using Xunit;

namespace HomeBlaze.AI.Tests.Mcp;

/// <summary>
/// Pins, through the real tools of an agent and of the external MCP server, that neither can change an automation, so that
/// neither can give an automation roles it does not hold itself.
/// </summary>
public sealed class AutomationMcpAccessTests : IDisposable
{
    private readonly Automation.Automation _automation;
    private readonly TestAutomationRoot _root;

    public AutomationMcpAccessTests()
    {
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithService<ILifecycleHandler>(() => new MethodPropertyInitializer(), handler => handler is MethodPropertyInitializer)
            .WithService<IPropertyLifecycleHandler>(() => new PropertyAttributeInitializer(), handler => handler is PropertyAttributeInitializer);
        _root = new TestAutomationRoot(context);
        _automation = new Automation.Automation(
            new SubjectPathResolver(() => _root),
            TimeProvider.System,
            new AllowAllAutomationAuthorizer(),
            new ServiceCollection().BuildServiceProvider(),
            NullLogger<Automation.Automation>.Instance)
        {
            Roles = [BuiltInRoles.OperatorName],
            Rules = new Dictionary<string, AutomationRule>
            {
                ["retract"] = new()
                {
                    Name = "Retract",
                    Mode = AutomationRuleMode.On,
                    Triggers = [new PropertyTrigger { Expression = "/Weather/Gust > 8" }],
                    Actions = [new InvokeOperationAction { Id = "close", OperationPath = "/Blinds/Close" }]
                }
            }
        };
        _root.Storm = _automation;
    }

    public void Dispose() => _automation.Dispose();

    [Theory]
    [InlineData(BuiltInRoles.EverythingName, "/Storm/Roles")]
    [InlineData(BuiltInRoles.EverythingName, "/Storm/Name")]
    [InlineData(BuiltInRoles.EverythingName, "/Storm/Enabled")]
    [InlineData(BuiltInRoles.EverythingName, "/Storm/Rules")]
    [InlineData(BuiltInRoles.EverythingName, "/Storm/Rules/retract/Mode")]
    [InlineData(BuiltInRoles.EverythingName, "/Storm/Rules/retract/Actions")]
    [InlineData(BuiltInRoles.OperatorName, "/Storm/Roles")]
    [InlineData(BuiltInRoles.OperatorName, "/Storm/Rules/retract/Actions")]
    public async Task WhenAnAgentOrAnMcpClientSetsAConfigurationPropertyOfAnAutomation_ThenTheWriteFailsAndTheAutomationIsUnchanged(string role, string path)
    {
        // Arrange
        var tools = CreateTools(TestAccessPolicies.Create(role));
        var rolesBefore = _automation.Roles;
        var rule = _automation.Rules["retract"];
        var actionsBefore = rule.Actions;

        // Act
        var result = await CallAsync(tools["set_property"], new { path, value = path.EndsWith("Mode") ? "Off" : (object)new[] { BuiltInRoles.EverythingName } });

        // Assert
        Assert.False(result.TryGetProperty("success", out _), result.ToString());
        Assert.NotNull(result.GetProperty("error").GetString());
        Assert.Same(rolesBefore, _automation.Roles);
        Assert.Same(actionsBefore, rule.Actions);
        Assert.Equal(AutomationRuleMode.On, rule.Mode);
    }

    [Fact]
    public async Task WhenAnOperatorResetsTheStateOfAnAutomation_ThenThePolicyAllowsTheCall()
    {
        // Arrange
        var tools = CreateTools(TestAccessPolicies.Operator);

        // Act
        var result = await CallAsync(tools[HomeBlazeMcpToolProvider.InvokeOperationToolName], new { path = "/Storm", method = nameof(Automation.Automation.ResetState) });

        // Assert
        Assert.Equal("Method invocation failed. Check server logs for details.", result.GetProperty("error").GetString());
    }

    [Fact]
    public async Task WhenAReaderResetsTheStateOfAnAutomation_ThenTheCallIsRefused()
    {
        // Arrange
        var tools = CreateTools(TestAccessPolicies.Create(BuiltInRoles.ReaderName));

        // Act
        var result = await CallAsync(tools[HomeBlazeMcpToolProvider.InvokeOperationToolName], new { path = "/Storm", method = nameof(Automation.Automation.ResetState) });

        // Assert
        Assert.Equal("Operation 'ResetState' is not permitted. The access policy does not allow it.", result.GetProperty("error").GetString());
    }

    private Dictionary<string, McpToolInfo> CreateTools(IMcpAccessPolicy accessPolicy)
    {
        var configuration = HomeBlazeMcpConfigurationFactory.Create(
            HomeBlazeMcpConfigurationFactoryTests.CreateServices(), () => _root, accessPolicy);
        return new McpToolFactory(_root, configuration).CreateTools().ToDictionary(tool => tool.Name);
    }

    private static async Task<JsonElement> CallAsync(McpToolInfo tool, object input) =>
        JsonSerializer.SerializeToElement(await tool.Handler(JsonSerializer.SerializeToElement(input), CancellationToken.None));
}

[InterceptorSubject]
public partial class TestAutomationRoot
{
    public partial Automation.Automation? Storm { get; set; }
}
