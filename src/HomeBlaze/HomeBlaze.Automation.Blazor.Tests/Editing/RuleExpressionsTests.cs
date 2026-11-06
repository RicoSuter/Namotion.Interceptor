using HomeBlaze.Automation.Blazor.Editing;
using HomeBlaze.Automation.Blazor.Tests.Testing;

namespace HomeBlaze.Automation.Blazor.Tests.Editing;

public class RuleExpressionsTests
{
    [Fact]
    public void WhenExpressionsAreChecked_ThenTheVariablesOfTheirPlaceApply()
    {
        // Arrange
        var expressions = new RuleExpressions(TestAutomations.Create(), "rule", allowsEvent: false);

        // Act
        var condition = expressions.Validate("/a/B > 1");
        var eventInCondition = expressions.Validate("event/State == On");
        var eventInFilter = RuleExpressions.ValidateFilter("event/State == On");
        var template = expressions.ValidateTemplate("Value {{ /a/B }}");

        // Assert
        Assert.Null(condition);
        Assert.NotNull(eventInCondition);
        Assert.Null(eventInFilter);
        Assert.Null(template);
    }

    [Fact]
    public void WhenAnExpressionIsPreviewed_ThenItHasItsResultOrNothingWhenInvalid()
    {
        // Arrange
        var expressions = new RuleExpressions(TestAutomations.Create(), "rule", allowsEvent: false);

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
        // Arrange
        var expressions = new RuleExpressions(TestAutomations.Create(), "rule", allowsEvent: false);

        // Act
        var text = expressions.RenderTemplate("Sum {{ 1 + 2 | raw }}");

        // Assert
        Assert.Equal("Sum 3", text);
    }
}
