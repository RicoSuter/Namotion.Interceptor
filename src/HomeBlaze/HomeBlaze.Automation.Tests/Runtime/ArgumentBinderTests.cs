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

    private readonly TestGraph _graph = new();
    private readonly TestSensor _sensor = new();
    private readonly AutomationRule _rule = new();

    public ArgumentBinderTests()
    {
        _graph.Add("Weather", _sensor);
        _graph.Add("Host", new TestRuleHost { Rules = new Dictionary<string, AutomationRule> { ["r1"] = _rule } });
        _sensor.Gust = 9;
        _sensor.Name = "WS90";
    }

    private ArgumentBinder CreateBinder(bool allowsEvent = false) =>
        new(new AutomationEvaluation(_graph.Resolver, _rule, Now, TimeZoneInfo.Utc), allowsEvent);

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

    [Fact]
    public void WhenAnExpressionArgumentIsUnknown_ThenBindingFailsNamingThePath()
    {
        // Arrange
        var binder = CreateBinder();

        // Act
        var isBound = binder.TryBindParameters([Parameter<int>("speed")], new Dictionary<string, string> { ["speed"] = "/Weather/Rain + 1" }, out _, out var error);

        // Assert
        Assert.False(isBound);
        Assert.Equal("The argument 'speed' is unknown, missing /Weather/Rain", error);
    }

    [Fact]
    public void WhenARequiredArgumentIsMissing_ThenBindingFails()
    {
        // Arrange
        var binder = CreateBinder();

        // Act
        var isBound = binder.TryBindParameters([Parameter<int>("speed")], new Dictionary<string, string>(), out _, out var error);

        // Assert
        Assert.False(isBound);
        Assert.Equal("The argument 'speed' is missing", error);
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
    public void WhenAnExpressionArgumentIsBlank_ThenBindingFailsWithTheValueIsEmpty()
    {
        // Arrange
        var binder = CreateBinder();

        // Act
        var isBound = binder.TryBindParameters([Parameter<int>("speed")], new Dictionary<string, string> { ["speed"] = " " }, out _, out var error);

        // Assert
        Assert.False(isBound);
        Assert.Equal("The argument 'speed' is invalid: the value is empty", error);
    }

    [Fact]
    public void WhenAnArgumentReadsTheEventWithoutAnEventTrigger_ThenBindingFails()
    {
        // Arrange
        var binder = CreateBinder(allowsEvent: false);

        // Act
        var isBound = binder.TryBindParameters([Parameter<string>("message")], new Dictionary<string, string> { ["message"] = "{{ event/Gesture }}" }, out _, out var error);

        // Assert
        Assert.False(isBound);
        Assert.Equal("The argument 'message' is invalid: 'event' is only available in event filters and in the arguments of a rule with an event trigger", error);
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
    public void WhenAnArgumentKeyMatchesNoParameter_ThenBindingFails()
    {
        // Arrange
        var binder = CreateBinder();
        var arguments = new Dictionary<string, string> { ["speed"] = "5", ["Sped"] = "6" };

        // Act
        var isBound = binder.TryBindParameters([Parameter<int>("speed")], arguments, out _, out var error);

        // Assert
        Assert.False(isBound);
        Assert.Equal("The argument 'Sped' matches no parameter of the operation", error);
    }

    [Fact]
    public void WhenAMemberKeyMatchesNoMemberOfTheRecord_ThenBindingFails()
    {
        // Arrange
        var binder = CreateBinder();
        var arguments = new Dictionary<string, string> { ["notification.Message"] = "Hi", ["notification.Typo"] = "x" };

        // Act
        var isBound = binder.TryBindParameters([Parameter<Notification>("notification")], arguments, out _, out var error);

        // Assert
        Assert.False(isBound);
        Assert.Equal("The argument 'notification.Typo' matches no member of 'notification'", error);
    }

    [Fact]
    public void WhenAParameterHasAWholeKeyAndMemberKeys_ThenBindingFails()
    {
        // Arrange
        var binder = CreateBinder();
        var arguments = new Dictionary<string, string> { ["notification"] = "Hi", ["notification.Message"] = "Hi" };

        // Act
        var isBound = binder.TryBindParameters([Parameter<Notification>("notification")], arguments, out _, out var error);

        // Assert
        Assert.False(isBound);
        Assert.Equal("The argument 'notification' is given both whole and per member", error);
    }

    [Fact]
    public void WhenAnArgumentReadsTheEventWithAnEventTrigger_ThenItBindsTheEventValue()
    {
        // Arrange
        var payload = new TestPressEvent { Timestamp = Now, DeviceId = "b1", Gesture = "Release" };
        var evaluation = new AutomationEvaluation(_graph.Resolver, _rule, Now, TimeZoneInfo.Utc, new CapturedEvent(_sensor, payload));
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
}
