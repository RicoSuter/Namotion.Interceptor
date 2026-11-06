using HomeBlaze.Automation.Tests.Testing;
using HomeBlaze.Services;
using static HomeBlaze.Automation.Tests.Testing.RuleBuilder;

namespace HomeBlaze.Automation.Tests;

public class AutomationPreviewTests
{
    private static Task<AutomationTestHost> StartAsync() => AutomationTestHost.StartAsync(automation =>
        automation.Rules = Keyed(("retract", On("Retract", When("/Weather/Gust > 8"), Invoke("/Blinds/Close")))));

    [Fact]
    public async Task WhenAConditionIsEvaluated_ThenItsResultAndValueAreTheCurrentOnes()
    {
        // Arrange
        await using var host = await StartAsync();
        host.Weather.Gust = 9;

        // Act
        var condition = AutomationPreview.Evaluate(host.Automation, "retract", "/Weather/Gust > 8", allowsEvent: false);
        var value = AutomationPreview.Evaluate(host.Automation, "retract", "/Weather/Gust", allowsEvent: false);

        // Assert
        Assert.Null(condition.Error);
        Assert.True(condition.Result);
        Assert.Empty(condition.MissingPaths);
        Assert.Equal(9.0, value.Value);
        Assert.False(string.IsNullOrEmpty(value.Text));
    }

    [Fact]
    public async Task WhenAValueIsMissing_ThenTheResultIsUnknownWithTheMissingPath()
    {
        // Arrange
        await using var host = await StartAsync();

        // Act
        var preview = AutomationPreview.Evaluate(host.Automation, "retract", "/Weather/Rain > 0", allowsEvent: false);

        // Assert
        Assert.Null(preview.Result);
        Assert.Equal(["/Weather/Rain"], preview.MissingPaths.ToArray());
    }

    [Fact]
    public async Task WhenARelativePathIsEvaluated_ThenItStartsAtTheSavedRuleAndAtNothingForANewRule()
    {
        // Arrange
        await using var host = await StartAsync();

        // Act
        var saved = AutomationPreview.Evaluate(host.Automation, "retract", "./Name", allowsEvent: false);
        var unsaved = AutomationPreview.Evaluate(host.Automation, "new", "./Name", allowsEvent: false);

        // Assert
        Assert.Equal("Retract", saved.Value);
        Assert.Null(unsaved.Value);
        Assert.Equal(["./Name"], unsaved.MissingPaths.ToArray());
    }

    [Fact]
    public async Task WhenTheExpressionIsInvalid_ThenThePreviewHasTheError()
    {
        // Arrange
        await using var host = await StartAsync();

        // Act
        var preview = AutomationPreview.Evaluate(host.Automation, "retract", "event/Gesture == Release", allowsEvent: false);

        // Assert
        Assert.Equal("'event' is only available in event filters and in the arguments of a rule with an event trigger", preview.Error);
        Assert.Null(preview.Result);
    }

    [Fact]
    public async Task WhenATemplateIsRendered_ThenItHasTheCurrentValues()
    {
        // Arrange
        await using var host = await StartAsync();
        host.Weather.Gust = 9;

        // Act
        var preview = AutomationPreview.Render(host.Automation, "retract", "Gust {{ /Weather/Gust | raw }} m/s", allowsEvent: false);

        // Assert
        Assert.Null(preview.Error);
        Assert.Equal("Gust 9 m/s", preview.Text);
    }

    [Fact]
    public async Task WhenTheTargetOfAnActionIsResolved_ThenItIsTheOperation()
    {
        // Arrange
        await using var host = await StartAsync();

        // Act
        var target = AutomationPreview.ResolveTarget(host.Automation, null, "/Blinds/Close");

        // Assert
        Assert.NotNull(target);
        Assert.NotNull(target.TryGetMethodMetadata());
        Assert.Null(AutomationPreview.ResolveTarget(host.Automation, null, "/Blinds/Missing"));
    }
}
