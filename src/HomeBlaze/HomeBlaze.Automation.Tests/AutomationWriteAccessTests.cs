using System.Reflection;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Authorization;
using HomeBlaze.Automation.Tests.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Authorization;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
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

        // Act
        var writableOfAutomation = ConfigurationMembers(_automation).Where(property => policy.CanWrite(property)).Select(property => property.Name).ToArray();
        var writableOfRule = ConfigurationMembers(rule).Where(property => policy.CanWrite(property)).Select(property => property.Name).ToArray();

        // Assert
        Assert.Empty(writableOfAutomation);
        Assert.Empty(writableOfRule);
    }

    [Fact]
    public void WhenTheConfigurationMembersAreEnumerated_ThenTheOnesOfAutomationsAreFound()
    {
        // Act
        var automationMembers = ConfigurationMembers(_automation).Select(property => property.Name).ToArray();
        var ruleMembers = ConfigurationMembers(_automation.Rules["retract"]).Select(property => property.Name).ToArray();

        // Assert
        Assert.Contains(nameof(Automation.Roles), automationMembers);
        Assert.Contains(nameof(Automation.Name), automationMembers);
        Assert.Contains(nameof(Automation.InitialState), automationMembers);
        Assert.Contains(nameof(AutomationRule.Actions), ruleMembers);
        Assert.Contains(nameof(AutomationRule.InState), ruleMembers);
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

    private static IEnumerable<RegisteredSubjectProperty> ConfigurationMembers(IInterceptorSubject subject) =>
        subject.GetType().GetProperties()
            .Where(property => property.GetCustomAttribute<ConfigurationAttribute>() is not null)
            .Select(property => subject.TryGetRegisteredProperty(property.Name)!);

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
