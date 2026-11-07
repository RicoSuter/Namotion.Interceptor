using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using HomeBlaze.Automation.Blazor.Editing;
using HomeBlaze.Automation.Blazor.Tests.Testing;
using HomeBlaze.Components.Abstractions.TimeZones;

namespace HomeBlaze.Automation.Blazor.Tests.Editing;

public class RuleExpressionsTests
{
    [Fact]
    public void WhenExpressionsAreChecked_ThenTheVariablesOfTheirPlaceApply()
    {
        // Arrange
        var expressions = TestAutomations.Expressions();

        // Act
        var eventInCondition = expressions.Validate("event/State == On");
        var eventInFilter = RuleExpressions.ValidateFilter("event/State == On");
        var template = expressions.ValidateTemplate("Value {{ /a/B }}");

        // Assert
        Assert.NotNull(eventInCondition);
        Assert.Null(eventInFilter);
        Assert.Null(template);
    }

    [Fact]
    public void WhenAnExpressionIsPreviewed_ThenItHasItsResultOrNothingWhenInvalid()
    {
        // Arrange
        var expressions = TestAutomations.Expressions();

        // Act
        var valid = expressions.Preview("2 > 1");
        var invalid = expressions.Preview("2 >");

        // Assert
        Assert.NotNull(valid);
        Assert.True(valid.Result);
        Assert.Null(invalid);
    }

    [Fact]
    public void WhenATemplateIsRendered_ThenItHasItsText()
    {
        // Act
        var text = TestAutomations.Expressions().RenderTemplate("Sum {{ 1 + 2 | raw }}");

        // Assert
        Assert.Equal("Sum 3", text);
    }

    [Fact]
    public void WhenAnExpressionIsPreviewed_ThenItsRelativePathsStartAtTheSavedRuleAndItsInstantsUseTheViewerZone()
    {
        // Arrange
        var viewerZone = new TimeZoneDisplayService();
        viewerZone.SetResolved(TimeZonePreference.Automatic, TimeZoneInfo.CreateCustomTimeZone("plus5", TimeSpan.FromHours(5), "plus5", "plus5"));
        var automation = TestAutomations.Create();
        ((IInterceptorSubject)automation).Context.AddFallbackContext(InterceptorSubjectContext.Create().WithRegistry().WithParents());
        automation.Rules = new Dictionary<string, AutomationRule> { ["rule"] = new() { Name = "Rule" } };
        var withViewerZone = new RuleExpressions(automation, "rule", allowsEvent: false, viewerZone);

        // Act
        var relative = withViewerZone.Preview("./Name == Rule");
        var viewer = withViewerZone.Preview("now");
        var server = new RuleExpressions(automation, "rule", allowsEvent: false).Preview("now");

        // Assert
        Assert.True(relative?.Result);
        Assert.Equal("Rule", withViewerZone.RenderTemplate("{{ ./Name }}"));
        Assert.NotNull(viewer);
        Assert.NotNull(server);
        Assert.Contains("+05:00", viewer.Text);
        Assert.NotEqual(viewer.Text, server.Text);
    }
}
