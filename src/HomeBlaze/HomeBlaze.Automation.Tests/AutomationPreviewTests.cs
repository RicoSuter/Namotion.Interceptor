using HomeBlaze.Automation.Tests.Testing;
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
        var condition = AutomationPreview.Evaluate(host.Automation, "retract", "/Weather/Gust > 8", allowsEvent: false);
        var value = AutomationPreview.Evaluate(host.Automation, "retract", "/Weather/Gust", allowsEvent: false);

        // Assert
        Assert.Null(condition.Error);
        Assert.True(condition.Result);
        Assert.Equal(9.0, value.Value);
        Assert.False(string.IsNullOrEmpty(value.Text));
    }

    [Fact]
    public async Task WhenAValueIsMissing_ThenTheResultIsUnknown()
    {
        // Arrange
        await using var host = await StartAsync();

        // Act
        var preview = AutomationPreview.Evaluate(host.Automation, "retract", "/Weather/Rain > 0", allowsEvent: false);

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
        var saved = AutomationPreview.Evaluate(host.Automation, "retract", "./Name", allowsEvent: false);
        var unsaved = AutomationPreview.Evaluate(host.Automation, "new", "./Name", allowsEvent: false);

        // Assert
        Assert.Equal("Retract", saved.Value);
        Assert.Null(unsaved.Value);
        Assert.Null(unsaved.Text);
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

    [Fact]
    public async Task WhenTheExpressionIsNotBoolean_ThenTheResultIsNullAndTheValueIsKept()
    {
        // Arrange
        await using var host = await StartAsync();
        host.Weather.Gust = 9;

        // Act
        var preview = AutomationPreview.Evaluate(host.Automation, "retract", "/Weather/Gust", allowsEvent: false);

        // Assert
        Assert.Null(preview.Result);
        Assert.Equal(9.0, preview.Value);
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
        var viewer = AutomationPreview.Evaluate(host.Automation, "retract", "/Weather/LastUpdated", allowsEvent: false, zone);
        var server = AutomationPreview.Evaluate(host.Automation, "retract", "/Weather/LastUpdated", allowsEvent: false);

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
        var secret = AutomationPreview.Evaluate(host.Automation, "retract", "/Weather/ApiKey", allowsEvent: false);
        var excluded = AutomationPreview.Evaluate(host.Automation, "retract", "/Weather/RawPayload", allowsEvent: false);
        var template = AutomationPreview.Render(host.Automation, "retract", "{{ /Weather/ApiKey | raw }}|{{ /Weather/RawPayload | raw }}", allowsEvent: false);

        // Assert
        Assert.Null(secret.Value);
        Assert.Null(secret.Text);
        Assert.Null(excluded.Value);
        Assert.DoesNotContain("secret", template.Text);
        Assert.DoesNotContain("payload", template.Text);
    }

    [Fact]
    public async Task WhenTheCallerMayNotReadAValue_ThenThePreviewTreatsItAsMissing()
    {
        // Arrange
        await using var host = await StartAsync();
        host.Weather.Gust = 12345;

        // Act
        AutomationExpressionPreview condition;
        AutomationExpressionPreview template;
        using (PropertyReadAccess.Enter(property => property.Name != nameof(TestSensor.Gust)))
        {
            condition = AutomationPreview.Evaluate(host.Automation, "retract", "/Weather/Gust > 8", allowsEvent: false);
            template = AutomationPreview.Render(host.Automation, "retract", "Gust {{ /Weather/Gust | raw }}", allowsEvent: false);
        }

        // Assert
        Assert.Null(condition.Result);
        Assert.Null(condition.Value);
        Assert.Null(condition.Text);
        Assert.Equal("Gust ", template.Text);
    }
}
