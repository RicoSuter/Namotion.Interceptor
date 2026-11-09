using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Messaging;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Automation.Expressions;
using HomeBlaze.Automation.Runtime;
using HomeBlaze.Automation.Tests.Testing;

namespace HomeBlaze.Automation.Tests.Runtime;

public class ArgumentBinderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    private readonly RuleGraph _graph = new();

    private ArgumentBinder CreateBinder() =>
        new(new AutomationEvaluation(_graph.Resolver, _graph.Rule, Now, TimeZoneInfo.Utc), allowsEvent: false);

    private static MethodParameter Parameter<T>(string name, bool isNullable = false) =>
        new() { Name = name, Type = typeof(T), IsNullable = isNullable };

    [Theory]
    [InlineData("300", 300, "speed: 300")]
    [InlineData("/Weather/Gust * 10", 90, "speed: 90")]
    public void WhenAnArgumentIsAConstantOrAnExpression_ThenItIsConvertedToTheParameterType(string text, int expected, string expectedDisplay)
    {
        // Arrange
        var binder = CreateBinder();

        // Act
        var isBound = binder.TryBindParameters([Parameter<int>("speed")], new Dictionary<string, string> { ["speed"] = text }, out var values, out var error);

        // Assert
        Assert.True(isBound, error);
        Assert.Equal(expected, Assert.Single(values!));
        Assert.Equal(expectedDisplay, binder.Display);
    }

    [Fact]
    public void WhenAStringArgumentIsATemplate_ThenItRendersAndAMissingValueStaysEmpty()
    {
        // Arrange
        var binder = CreateBinder();
        var arguments = new Dictionary<string, string> { ["message"] = "Sensor {{ /Weather/Name }} rain {{ /Weather/Rain }}" };

        // Act
        var isBound = binder.TryBindParameters([Parameter<string>("message")], arguments, out var values, out var error);

        // Assert
        Assert.True(isBound, error);
        Assert.Equal("Sensor WS90 rain ", Assert.Single(values!));
        Assert.Equal("message: \"Sensor WS90 rain \"", binder.Display);
    }

    [Fact]
    public void WhenARecordParameterIsBoundPerMember_ThenItsStringMembersAreTemplates()
    {
        // Arrange
        var binder = CreateBinder();
        var arguments = new Dictionary<string, string>
        {
            ["notification.Message"] = "Gust {{ /Weather/Gust | raw }} m/s",
            ["Notification.Priority"] = "High"
        };

        // Act
        var isBound = binder.TryBindParameters([Parameter<Notification>("notification")], arguments, out var values, out var error);

        // Assert
        Assert.True(isBound, error);
        var notification = Assert.IsType<Notification>(Assert.Single(values!));
        Assert.Equal("Gust 9 m/s", notification.Message);
        Assert.Equal(NotificationPriority.High, notification.Priority);
    }

    public static TheoryData<MethodParameter, Dictionary<string, string>, string> BindingFailures => new()
    {
        { Parameter<int>("speed"), new() { ["speed"] = "/Weather/Rain + 1" }, "The argument 'speed' is unknown, missing /Weather/Rain" },
        { Parameter<int>("speed"), [], "The argument 'speed' is missing" },
        {
            Parameter<string>("message"), new() { ["message"] = "{{ event/Gesture }}" },
            "The argument 'message' is invalid: 'event' is only available in event filters and in the arguments of a rule with an event trigger"
        },
        { Parameter<int>("speed"), new() { ["speed"] = "5", ["Sped"] = "6" }, "The argument 'Sped' matches no parameter of the operation" },
        {
            Parameter<Notification>("notification"), new() { ["notification.Message"] = "Hi", ["notification.Typo"] = "x" },
            "The argument 'notification.Typo' matches no member of 'notification'"
        },
        {
            Parameter<Notification>("notification"), new() { ["notification"] = "Hi", ["notification.Message"] = "Hi" },
            "The argument 'notification' is given both whole and per member"
        },
        { Parameter<int>("speed"), new() { ["speed"] = "1", ["Speed"] = "2" }, "The argument 'Speed' is given twice" },
        {
            Parameter<Notification>("notification"), new() { ["notification.Message"] = "Hi", ["Notification.message"] = "Ho" },
            "The argument 'Notification.message' is given twice"
        },
        { Parameter<TimeSpan>("delay"), new() { ["delay"] = "300" }, "The argument 'delay' cannot be converted to TimeSpan: 300" },
        { Parameter<int>("speed"), new() { ["speed"] = "0x10" }, "The argument 'speed' is invalid: invalid number '0x10' at position 0" },
        { Parameter<double>("level"), new() { ["level"] = "1e400" }, "The argument 'level' is invalid: number out of range '1e400' at position 0" },
        { Parameter<double>("level"), new() { ["level"] = "NaN" }, "The argument 'level' is unknown, missing NaN" }
    };

    [Theory]
    [MemberData(nameof(BindingFailures))]
    public void WhenTheArgumentsDoNotFitTheParameter_ThenBindingFailsWithTheReason(MethodParameter parameter, Dictionary<string, string> arguments, string expectedError)
    {
        // Arrange
        var binder = CreateBinder();

        // Act
        var isBound = binder.TryBindParameters([parameter], arguments, out _, out var error);

        // Assert
        Assert.False(isBound);
        Assert.Equal(expectedError, error);
    }

    [Fact]
    public void WhenANullableArgumentIsMissing_ThenItIsNull()
    {
        // Arrange
        var binder = CreateBinder();

        // Act
        var isBound = binder.TryBindParameters([Parameter<int?>("speed", isNullable: true)], new Dictionary<string, string>(), out var values, out var error);

        // Assert
        Assert.True(isBound, error);
        Assert.Null(Assert.Single(values!));
    }

    [Fact]
    public void WhenAParameterNeedsNoInput_ThenItIsLeftOut()
    {
        // Arrange
        var binder = CreateBinder();
        MethodParameter[] parameters =
        [
            Parameter<int>("speed"),
            new() { Name = "cancellationToken", Type = typeof(CancellationToken), IsRuntimeProvided = true }
        ];

        // Act
        var isBound = binder.TryBindParameters(parameters, new Dictionary<string, string> { ["speed"] = "5" }, out var values, out var error);

        // Assert
        Assert.True(isBound, error);
        Assert.Equal(5, Assert.Single(values!));
    }

    [Fact]
    public void WhenAConstantIsBoundToAPercentParameter_ThenItIsTheRawStoredValue()
    {
        // Arrange
        var binder = CreateBinder();
        var parameter = new MethodParameter { Name = "volume", Type = typeof(decimal), Unit = StateUnit.Percent };

        // Act
        var isBound = binder.TryBindParameters([parameter], new Dictionary<string, string> { ["volume"] = "0.5" }, out var values, out var error);

        // Assert
        Assert.True(isBound, error);
        Assert.Equal(0.5m, Assert.Single(values!));
    }

    [Fact]
    public void WhenAnArgumentReadsTheEventWithAnEventTrigger_ThenItBindsTheEventValue()
    {
        // Arrange
        var payload = new TestPressEvent { Timestamp = Now, DeviceId = "b1", Gesture = "Release" };
        var evaluation = new AutomationEvaluation(_graph.Resolver, _graph.Rule, Now, TimeZoneInfo.Utc, new CapturedEvent(_graph.Weather, payload));
        var binder = new ArgumentBinder(evaluation, allowsEvent: true);

        // Act
        var isBound = binder.TryBindParameters([Parameter<string>("message")], new Dictionary<string, string> { ["message"] = "{{ event/Gesture }}" }, out var values, out var error);

        // Assert
        Assert.True(isBound, error);
        Assert.Equal("Release", Assert.Single(values!));
    }

    [Fact]
    public void WhenARecordParameterLacksARequiredMember_ThenBindingFailsWithTheError()
    {
        // Arrange
        var binder = CreateBinder();
        var arguments = new Dictionary<string, string> { ["notification.Title"] = "Hi" };

        // Act
        var isBound = binder.TryBindParameters([Parameter<Notification>("notification")], arguments, out _, out var error);

        // Assert
        Assert.False(isBound);
        Assert.StartsWith("The argument 'notification' is invalid: ", error);
        Assert.Contains("Message", error);
    }

    [Fact]
    public void WhenAnEnumArgumentIsAConstant_ThenItIsBoundByName()
    {
        // Arrange
        var binder = CreateBinder();

        // Act
        var isBound = binder.TryBindParameters([Parameter<NotificationPriority>("priority")], new Dictionary<string, string> { ["priority"] = "high" }, out var values, out var error);

        // Assert
        Assert.True(isBound, error);
        Assert.Equal(NotificationPriority.High, Assert.Single(values!));
    }

    [Theory]
    [InlineData("5m", 0, 5, 0, "delay: 5m")]
    [InlineData("1h30m", 1, 30, 0, "delay: 1h30m")]
    [InlineData("00:00:30", 0, 0, 30, "delay: 30s")]
    public void WhenATimeSpanArgumentIsADuration_ThenItIsBound(string text, int hours, int minutes, int seconds, string expectedDisplay)
    {
        // Arrange
        var binder = CreateBinder();

        // Act
        var isBound = binder.TryBindParameters([Parameter<TimeSpan>("delay")], new Dictionary<string, string> { ["delay"] = text }, out var values, out var error);

        // Assert
        Assert.True(isBound, error);
        Assert.Equal(new TimeSpan(hours, minutes, seconds), Assert.Single(values!));
        Assert.Equal(expectedDisplay, binder.Display);
    }

    [Theory]
    [InlineData("now")]
    [InlineData("2026-11-06T12:00:00")]
    [InlineData("2026-11-06T13:00:00+01:00")]
    public void WhenADateTimeArgumentIsAnInstant_ThenItIsBoundInUtc(string text)
    {
        // Arrange
        var binder = CreateBinder();

        // Act
        var isBound = binder.TryBindParameters([Parameter<DateTime>("at")], new Dictionary<string, string> { ["at"] = text }, out var values, out var error);

        // Assert
        Assert.True(isBound, error);
        var at = Assert.IsType<DateTime>(Assert.Single(values!));
        Assert.Equal(Now.UtcDateTime, at);
        Assert.Equal(DateTimeKind.Utc, at.Kind);
    }

    [Fact]
    public void WhenAnInstantConstantHasNoOffset_ThenItIsReadAsUtcLikeInExpressions()
    {
        // Arrange
        var binder = CreateBinder();

        // Act
        var isBound = binder.TryBindParameters(
            [Parameter<DateTimeOffset>("at")], new Dictionary<string, string> { ["at"] = "2026-11-06T12:00:00" }, out var values, out var error);

        // Assert
        Assert.True(isBound, error);
        var at = Assert.IsType<DateTimeOffset>(Assert.Single(values!));
        Assert.Equal(Now, at);
        Assert.Equal(TimeSpan.Zero, at.Offset);
    }

    [Theory]
    [InlineData("2.5", 3)]
    [InlineData("-2.5", -3)]
    public void WhenAFractionIsBoundToAnInteger_ThenItIsRoundedHalfAwayFromZero(string text, int expected)
    {
        // Arrange
        var binder = CreateBinder();

        // Act
        var isBound = binder.TryBindParameters([Parameter<int>("speed")], new Dictionary<string, string> { ["speed"] = text }, out var values, out var error);

        // Assert
        Assert.True(isBound, error);
        Assert.Equal(expected, Assert.Single(values!));
    }

    [Fact]
    public void WhenARecordMemberTemplateRendersEmpty_ThenTheMemberIsTheEmptyText()
    {
        // Arrange
        var binder = CreateBinder();
        var arguments = new Dictionary<string, string>
        {
            ["notification.Message"] = "{{ /Weather/Rain }}",
            ["notification.Title"] = "{{ /Weather/Rain }}"
        };

        // Act
        var isBound = binder.TryBindParameters([Parameter<Notification>("notification")], arguments, out var values, out var error);

        // Assert
        Assert.True(isBound, error);
        var notification = Assert.IsType<Notification>(Assert.Single(values!));
        Assert.Equal(string.Empty, notification.Message);
        Assert.Equal(string.Empty, notification.Title);
    }
}
