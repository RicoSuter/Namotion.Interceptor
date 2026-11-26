using HomeBlaze.Automation.Tests.Testing;
using HomeBlaze.Components.Abstractions.Expressions;
using HomeBlaze.Components.Abstractions.TimeZones;
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
        var condition = Evaluate(host.Automation, "retract", "/Weather/Gust > 8");
        var value = Evaluate(host.Automation, "retract", "/Weather/Gust");

        // Assert
        Assert.True(condition.Result);
        Assert.Null(value.Result);
        Assert.Equal(9.0, value.Value);
        Assert.False(string.IsNullOrEmpty(value.Text));
    }

    [Fact]
    public async Task WhenAParsedExpressionAndTemplateAreEvaluatedAgain_ThenTheyHaveTheCurrentValues()
    {
        // Arrange
        await using var host = await StartAsync();
        host.Weather.Gust = 9;
        Assert.True(AutomationValidator.TryParseExpression("/Weather/Gust > 8", allowsEvent: false, out var expression, out var expressionError), expressionError);
        Assert.True(AutomationValidator.TryParseTemplate("Gust {{ /Weather/Gust | raw }}", allowsEvent: false, out var template, out var templateError), templateError);

        // Act
        var before = AutomationPreview.Evaluate(host.Automation, "retract", expression);
        var renderedBefore = AutomationPreview.Render(host.Automation, "retract", template);
        host.Weather.Gust = 5;
        var after = AutomationPreview.Evaluate(host.Automation, "retract", expression);
        var renderedAfter = AutomationPreview.Render(host.Automation, "retract", template);

        // Assert
        Assert.True(before.Result);
        Assert.False(after.Result);
        Assert.Equal("Gust 9", renderedBefore);
        Assert.Equal("Gust 5", renderedAfter);
        Assert.False(AutomationValidator.TryParseExpression("/Weather/Gust >", allowsEvent: false, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public async Task WhenAValueIsMissing_ThenTheResultIsUnknown()
    {
        // Arrange
        await using var host = await StartAsync();

        // Act
        var preview = Evaluate(host.Automation, "retract", "/Weather/Rain > 0");

        // Assert
        Assert.Null(preview.Result);
        Assert.Null(preview.Value);
    }

    [Fact]
    public async Task WhenARelativePathIsEvaluated_ThenItStartsAtTheSavedRuleAndAtNothingForANewRule()
    {
        // Arrange
        await using var host = await StartAsync();

        // Act
        var saved = Evaluate(host.Automation, "retract", "./Name");
        var unsaved = Evaluate(host.Automation, "new", "./Name");

        // Assert
        Assert.Equal("Retract", saved.Value);
        Assert.Null(unsaved.Value);
        Assert.Null(unsaved.Text);
    }

    [Fact]
    public async Task WhenATemplateIsRendered_ThenItHasTheCurrentValues()
    {
        // Arrange
        await using var host = await StartAsync();
        host.Weather.Gust = 9;

        // Act
        var preview = Render(host.Automation, "retract", "Gust {{ /Weather/Gust | raw }} m/s");

        // Assert
        Assert.Equal("Gust 9 m/s", preview);
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

    [Fact]
    public async Task WhenADateIsEvaluatedWithATimeZone_ThenTheTextUsesIt()
    {
        // Arrange
        await using var host = await StartAsync();
        host.Weather.LastUpdated = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var zone = new TimeZoneDisplayService();
        zone.SetResolved(TimeZonePreference.Automatic, TimeZoneInfo.CreateCustomTimeZone("plus5", TimeSpan.FromHours(5), "plus5", "plus5"));

        // Act
        var viewer = Evaluate(host.Automation, "retract", "/Weather/LastUpdated", zone);
        var server = Evaluate(host.Automation, "retract", "/Weather/LastUpdated");

        // Assert
        Assert.Contains("+05:00", viewer.Text);
        Assert.NotEqual(viewer.Text, server.Text);
    }

    [Fact]
    public async Task WhenASecretOrExcludedValueIsPreviewed_ThenItIsMissing()
    {
        // Arrange
        await using var host = await StartAsync();
        host.Weather.ApiKey = "secret";
        host.Weather.RawPayload = "payload";

        // Act
        var secret = Evaluate(host.Automation, "retract", "/Weather/ApiKey");
        var excluded = Evaluate(host.Automation, "retract", "/Weather/RawPayload");
        var template = Render(host.Automation, "retract", "{{ /Weather/ApiKey | raw }}|{{ /Weather/RawPayload | raw }}");

        // Assert
        Assert.Null(secret.Value);
        Assert.Null(secret.Text);
        Assert.Null(excluded.Value);
        Assert.DoesNotContain("secret", template);
        Assert.DoesNotContain("payload", template);
    }

    [Fact]
    public async Task WhenReadingAValueThrows_ThenThePreviewSaysWhyInsteadOfThrowing()
    {
        // Arrange
        await using var host = await StartAsync();
        host.Weather.Reading = new UnreadableReading();

        // Act
        var value = Evaluate(host.Automation, "retract", "/Weather/Reading");
        var template = Render(host.Automation, "retract", "Reading {{ /Weather/Reading }}");

        // Assert
        Assert.Null(value.Result);
        Assert.Null(value.Value);
        Assert.Equal("cannot be read: The reading is corrupt", value.Text);
        Assert.Equal("cannot be read: The reading is corrupt", template);
    }

    private static ExpressionPreview Evaluate(Automation automation, string ruleKey, string expression, ITimeZoneDisplay? timeZone = null)
    {
        Assert.True(AutomationValidator.TryParseExpression(expression, allowsEvent: false, out var parsed, out var error), error);
        return AutomationPreview.Evaluate(automation, ruleKey, parsed, timeZone);
    }

    private static string Render(Automation automation, string ruleKey, string template)
    {
        Assert.True(AutomationValidator.TryParseTemplate(template, allowsEvent: false, out var parsed, out var error), error);
        return AutomationPreview.Render(automation, ruleKey, parsed);
    }
}
