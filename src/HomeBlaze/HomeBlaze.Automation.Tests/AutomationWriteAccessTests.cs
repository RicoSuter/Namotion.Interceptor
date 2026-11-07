using System.Reflection;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Authorization;
using HomeBlaze.Automation.Tests.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Authorization;
using Namotion.Interceptor.Registry;
using static HomeBlaze.Automation.Tests.Testing.RuleBuilder;

namespace HomeBlaze.Automation.Tests;

/// <summary>
/// Pins that no role lets an agent or an MCP client change an automation, so that neither can give an automation more
/// than a person did: the system rules deny writing its configuration and that of its rules, and neither type is a layout
/// block that the Markdown tools may add to a page.
/// </summary>
public sealed class AutomationWriteAccessTests : IDisposable
{
    private readonly TestGraph _graph = new();
    private readonly AuthorizationService _service;
    private readonly Automation _automation;

    public AutomationWriteAccessTests()
    {
        _service = TestAuthorization.CreateService(_graph);
        _automation = new Automation(
            _graph.Resolver, TimeProvider.System, new AllowAllAutomationAuthorizer(), new ServiceCollection().BuildServiceProvider(), NullLogger<Automation>.Instance)
        {
            Roles = [BuiltInRoles.OperatorName],
            Rules = Keyed(("retract", On("Retract", When("/Weather/Gust > 8"), Invoke("/Blinds/Close"))))
        };
        _graph.Add("Storm", _automation);
    }

    public void Dispose()
    {
        _automation.Dispose();
        _service.Dispose();
    }

    [Theory]
    [InlineData(BuiltInRoles.OperatorName)]
    [InlineData(BuiltInRoles.EverythingName)]
    public void WhenAPrincipalHasABroadRole_ThenItCannotWriteTheConfigurationOfAnAutomationOrOfItsRules(string role)
    {
        // Arrange
        var policy = _service.GetPolicy([role]).Policy;
        var rule = _automation.Rules["retract"];
        string[] automationMembers =
            [nameof(Automation.Roles), nameof(Automation.Rules), nameof(Automation.RuleOrder), nameof(Automation.States), nameof(Automation.Enabled)];
        string[] ruleMembers = [nameof(AutomationRule.Actions), nameof(AutomationRule.Mode), nameof(AutomationRule.Triggers), nameof(AutomationRule.Guard)];

        // Act
        var writableOfAutomation = automationMembers.Where(name => policy.CanWrite(_automation.TryGetRegisteredProperty(name)!)).ToArray();
        var writableOfRule = ruleMembers.Where(name => policy.CanWrite(rule.TryGetRegisteredProperty(name)!)).ToArray();

        // Assert
        Assert.Empty(writableOfAutomation);
        Assert.Empty(writableOfRule);
    }

    [Fact]
    public void WhenAnAgentIsAnOperator_ThenItMayResetTheStateButNotChangeTheRoles()
    {
        // Arrange
        var policy = _service.GetPolicy([BuiltInRoles.OperatorName]).Policy;

        // Act
        var canReset = policy.IsAllowed(_automation.TryGetRegisteredProperty(nameof(Automation.ResetState))!, AuthorizationActions.Execute);
        var canWriteRoles = policy.CanWrite(_automation.TryGetRegisteredProperty(nameof(Automation.Roles))!);

        // Assert
        Assert.True(canReset);
        Assert.False(canWriteRoles);
    }

    [Fact]
    public void WhenTheTypesOfAutomationsAreInspected_ThenNeitherIsALayoutBlockThatAnAgentMayAddToAPage()
    {
        // Act
        var automationLayout = typeof(Automation).GetCustomAttribute<PageLayoutAttribute>();
        var ruleLayout = typeof(AutomationRule).GetCustomAttribute<PageLayoutAttribute>();

        // Assert
        Assert.Null(automationLayout);
        Assert.Null(ruleLayout);
    }
}
