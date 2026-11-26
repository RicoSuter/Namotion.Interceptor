using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Devices;
using HomeBlaze.Automation.Expressions;
using HomeBlaze.Automation.Tests.Testing;
using Namotion.Interceptor.Expressions;

namespace HomeBlaze.Automation.Tests.Expressions;

public class AutomationEvaluationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 7, 45, 0, TimeSpan.Zero);

    private readonly RuleGraph _graph = new();

    private static ParsedExpression Parse(string text)
    {
        Assert.True(AutomationExpressionCheck.TryParseExpression(text, allowsEvent: true, out var expression, out var error), error);
        return expression;
    }

    private AutomationEvaluation CreateEvaluation(TimeZoneInfo? timeZone = null, CapturedEvent? capturedEvent = null) =>
        new(_graph.Resolver, _graph.Rule, Now, timeZone ?? TimeZoneInfo.Utc, capturedEvent);

    [Theory]
    [InlineData("/Weather/Gust > 8")]
    [InlineData("/Weather/Gust * 2 == 18")]
    [InlineData("/Weather/Name == WS90")]
    [InlineData("./LastTriggeredAt == null and ../EnteredAt != null")]
    public void WhenPathsResolve_ThenTheyReadFromTheRootTheRuleAndItsParent(string text)
    {
        // Arrange
        _graph.Host.EnteredAt = Now.AddMinutes(-11);
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
        _graph.Host.EnteredAt = Now.AddMinutes(-11);
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
        var evaluation = CreateEvaluation(capturedEvent: new CapturedEvent(_graph.Weather, payload));

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
        _graph.Weather.ApiKey = "secret";
        _graph.Weather.RawPayload = "{ \"key\": \"secret\" }";
        var evaluation = CreateEvaluation();

        // Act
        var result = evaluation.EvaluateCondition(Parse($"{path} == null"));

        // Assert
        Assert.True(result);
    }

    [Theory]
    [InlineData("event/Token")]
    [InlineData("event/Raw")]
    [InlineData("event/Credentials")]
    [InlineData("event")]
    public void WhenAnEventValueIsSecretOrExcludedFromExpressions_ThenItIsMissing(string path)
    {
        // Arrange
        var payload = new SecretEvent
        {
            Timestamp = Now,
            DeviceId = "b1",
            Token = "token-secret",
            Raw = "raw-secret",
            Credentials = new SecretCredentials { Password = "password-secret" }
        };
        var evaluation = CreateEvaluation(capturedEvent: new CapturedEvent(_graph.Weather, payload));
        Assert.True(AutomationExpressionCheck.TryParseTemplate($"{{{{ {path} | raw }}}}", allowsEvent: true, out var template, out var error), error);

        // Act
        var text = evaluation.Render(template);

        // Assert
        Assert.Equal(string.Empty, text);
        Assert.Equal([path], evaluation.MissingPaths.ToArray());
    }

    [Theory]
    [InlineData("event")]
    [InlineData("event/Inner")]
    [InlineData("event/Raw")]
    public void WhenAnEventValueHoldsAMemberExcludedFromExpressions_ThenItIsMissing(string path)
    {
        // Arrange
        var payload = new OverridingEvent
        {
            Timestamp = Now,
            DeviceId = "b1",
            Raw = "raw-secret",
            Inner = new ExcludedInner { Source = "source-secret" }
        };
        var evaluation = CreateEvaluation(capturedEvent: new CapturedEvent(_graph.Weather, payload));
        Assert.True(AutomationExpressionCheck.TryParseTemplate($"{{{{ {path} | raw }}}}", allowsEvent: true, out var template, out var error), error);

        // Act
        var text = evaluation.Render(template);

        // Assert
        Assert.Equal(string.Empty, text);
        Assert.Equal([path], evaluation.MissingPaths.ToArray());
    }

    public record ExcludedEvent : DeviceEvent
    {
        [ExcludeFromExpressions]
        public virtual string? Raw { get; init; }

        public ExcludedInner? Inner { get; init; }
    }

    public sealed record OverridingEvent : ExcludedEvent
    {
        public override string? Raw { get; init; }
    }

    public sealed record ExcludedInner
    {
        [ExcludeFromExpressions]
        public string? Source { get; init; }
    }

    public sealed record SecretEvent : DeviceEvent
    {
        [Configuration(IsSecret = true)]
        public string? Token { get; init; }

        [ExcludeFromExpressions]
        public string? Raw { get; init; }

        public SecretCredentials? Credentials { get; init; }
    }

    public sealed record SecretCredentials
    {
        [Configuration(IsSecret = true)]
        public string? Password { get; init; }
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
}
