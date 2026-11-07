using System.Globalization;
using HomeBlaze.Automation.Expressions;
using HomeBlaze.Automation.Tests.Testing;
using Namotion.Interceptor.Expressions;

namespace HomeBlaze.Automation.Tests.Expressions;

public class AutomationEvaluationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 7, 45, 0, TimeSpan.Zero);

    private readonly TestGraph _graph = new();
    private readonly TestSensor _sensor = new();
    private readonly AutomationRule _rule = new();
    private readonly TestRuleHost _host = new();

    public AutomationEvaluationTests()
    {
        _host.Rules = new Dictionary<string, AutomationRule> { ["r1"] = _rule };
        _graph.Add("Weather", _sensor);
        _graph.Add("Host", _host);
        _sensor.Gust = 9;
        _sensor.Name = "WS90";
    }

    private static ParsedExpression Parse(string text)
    {
        Assert.True(AutomationExpressionCheck.TryParseExpression(text, allowsEvent: true, out var expression, out var error), error);
        return expression;
    }

    private AutomationEvaluation CreateEvaluation(TimeZoneInfo? timeZone = null, CapturedEvent? capturedEvent = null) =>
        new(_graph.Resolver, _rule, Now, timeZone ?? TimeZoneInfo.Utc, capturedEvent);

    [Theory]
    [InlineData("/Weather/Gust > 8")]
    [InlineData("/Weather/Gust * 2 == 18")]
    [InlineData("/Weather/Name == WS90")]
    [InlineData("./LastTriggeredAt == null and ../EnteredAt != null")]
    public void WhenPathsResolve_ThenTheyReadFromTheRootTheRuleAndItsParent(string text)
    {
        // Arrange
        _host.EnteredAt = Now.AddMinutes(-11);
        var evaluation = CreateEvaluation();

        // Act
        var result = evaluation.EvaluateCondition(Parse(text));

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void WhenNowAndTimeAreUsed_ThenTheyAreTheInstantAndTheLocalTimeOfDay()
    {
        // Arrange
        _host.EnteredAt = Now.AddMinutes(-11);
        var oneHourAhead = TimeZoneInfo.CreateCustomTimeZone("Test+1", TimeSpan.FromHours(1), "Test+1", "Test+1");
        var evaluation = CreateEvaluation(oneHourAhead);

        // Act
        var elapsed = evaluation.EvaluateCondition(Parse("now - ../EnteredAt > 10m"));
        var morning = evaluation.EvaluateCondition(Parse("time >= 8h45m and time < 8h46m"));

        // Assert
        Assert.True(elapsed);
        Assert.True(morning);
    }

    [Fact]
    public void WhenAnEventIsCaptured_ThenEventReadsThePayloadAndSubjectThePublisher()
    {
        // Arrange
        var payload = new TestPressEvent { Timestamp = Now, DeviceId = "b1", Gesture = "Release" };
        var evaluation = CreateEvaluation(capturedEvent: new CapturedEvent(_sensor, payload));

        // Act
        var result = evaluation.EvaluateCondition(Parse("event/Gesture == Release and subject/Name == WS90 and event/DeviceId == b1"));

        // Assert
        Assert.True(result);
    }

    [Theory]
    [InlineData("/Weather/ApiKey")]
    [InlineData("/Weather/RawPayload")]
    public void WhenAValueIsSecretOrExcludedFromExpressions_ThenItIsMissing(string path)
    {
        // Arrange
        _sensor.ApiKey = "secret";
        _sensor.RawPayload = "{ \"key\": \"secret\" }";
        var evaluation = CreateEvaluation();

        // Act
        var result = evaluation.EvaluateCondition(Parse($"{path} == null"));

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void WhenValuesAreMissing_ThenTheConditionIsUnknownAndNamesThePaths()
    {
        // Arrange
        var evaluation = CreateEvaluation();

        // Act
        var result = evaluation.EvaluateCondition(Parse("/Weather/Rain > 0 or /Nowhere/Value > 1"));

        // Assert
        Assert.Null(result);
        Assert.Equal(["/Weather/Rain", "/Nowhere/Value"], evaluation.MissingPaths.ToArray());
    }

    [Fact]
    public void WhenATemplateIsRendered_ThenAMissingValueLeavesItsPlaceEmpty()
    {
        // Arrange
        var evaluation = CreateEvaluation();
        Assert.True(AutomationExpressionCheck.TryParseTemplate(
            "Sensor {{ /Weather/Name }}, gust {{ /Weather/Gust | raw }}, rain {{ /Weather/Rain }}.", allowsEvent: false, out var template, out var error), error);

        // Act
        var text = evaluation.Render(template);

        // Assert
        Assert.Equal("Sensor WS90, gust 9, rain .", text);
    }

    [Theory]
    [InlineData("2026-10-25T00:00:00Z", "02:00:00")] // the first 02:00 in Zurich, daylight saving time
    [InlineData("2026-10-25T00:59:59Z", "02:59:59")]
    [InlineData("2026-10-25T01:00:00Z", "02:59:59.9999999")] // the second 02:00, after the clocks went back
    [InlineData("2026-10-25T01:30:00Z", "02:59:59.9999999")]
    [InlineData("2026-10-25T02:00:00Z", "03:00:00")]
    public void WhenTheClockFallsBack_ThenTheTimeOfDayDoesNotMoveBackwards(string instant, string expected)
    {
        // Arrange
        var zurich = TimeZoneInfo.FindSystemTimeZoneById("Europe/Zurich");
        var evaluation = new AutomationEvaluation(
            _graph.Resolver, _rule, DateTimeOffset.Parse(instant, CultureInfo.InvariantCulture), zurich);

        // Act
        var timeOfDay = evaluation.TimeOfDay;

        // Assert
        Assert.Equal(TimeSpan.Parse(expected, CultureInfo.InvariantCulture), timeOfDay);
    }
}
