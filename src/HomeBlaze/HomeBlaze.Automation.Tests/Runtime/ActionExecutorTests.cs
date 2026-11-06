using HomeBlaze.Automation.Expressions;
using HomeBlaze.Automation.Runtime;
using HomeBlaze.Automation.Tests.Testing;
using Microsoft.Extensions.Time.Testing;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Automation.Tests.Runtime;

public class ActionExecutorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    private readonly TestGraph _graph = new();
    private readonly TestSensor _sensor = new();
    private readonly TestBlinds _blinds = new();
    private readonly TestNotifier _notifier = new();
    private readonly AutomationRule _rule = new();
    private readonly TestRuleHost _host = new();
    private readonly FakeTimeProvider _time = new(Now);

    public ActionExecutorTests()
    {
        _host.Rules = new Dictionary<string, AutomationRule> { ["r1"] = _rule };
        _graph.Add("Weather", _sensor);
        _graph.Add("Blinds", _blinds);
        _graph.Add("Notifier", _notifier);
        _graph.Add("Host", _host);
        _sensor.Gust = 9;
    }

    private ActionExecutor CreateExecutor(IAutomationAuthorizer? authorizer = null) =>
        new(_graph.Resolver, authorizer ?? new AllowAllAutomationAuthorizer(), serviceProvider: null, _time);

    private ActionScope CreateScope() =>
        new(_host, _rule, new AutomationEvaluation(_graph.Resolver, _rule, Now, TimeZoneInfo.Utc), AllowsEvent: false);

    private static InvokeOperationAction Invoke(string path, Dictionary<string, string>? arguments = null) =>
        new() { OperationPath = path, Arguments = arguments ?? new Dictionary<string, string>() };

    [Fact]
    public async Task WhenAnOperationIsInvoked_ThenItRunsWithItsBoundArguments()
    {
        // Arrange
        var executor = CreateExecutor();

        // Act
        var result = await executor.InvokeOperationAsync(Invoke("/Blinds/MoveTo", new() { ["position"] = "40" }), CreateScope(), CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("/Blinds/MoveTo(position: 40)", result.Message);
        Assert.Equal(40, _blinds.Position);
    }

    [Fact]
    public async Task WhenANotificationIsSent_ThenItsRecordIsBoundPerMember()
    {
        // Arrange
        var executor = CreateExecutor();
        var action = Invoke("/Notifier/SendNotification", new() { ["notification.Message"] = "Gust {{ /Weather/Gust | raw }} m/s" });

        // Act
        var result = await executor.InvokeOperationAsync(action, CreateScope(), CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("Gust 9 m/s", Assert.Single(_notifier.Sent).Message);
    }

    [Theory]
    [InlineData("/Blinds/Calibrate", "/Blinds/Calibrate requires confirmation by a person and cannot be called by an automation")]
    [InlineData("/Blinds/GetPosition", "/Blinds/GetPosition is a query, not an operation")]
    [InlineData("/Blinds/Nothing", "No operation at /Blinds/Nothing")]
    [InlineData("/Blinds/Jam", "/Blinds/Jam() failed: The motor is jammed.")]
    public async Task WhenAnOperationCannotRun_ThenTheResultIsAFailureWithTheReason(string path, string expected)
    {
        // Arrange
        var executor = CreateExecutor();

        // Act
        var result = await executor.InvokeOperationAsync(Invoke(path), CreateScope(), CancellationToken.None);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(expected, result.Message);
    }

    [Fact]
    public async Task WhenAnOperationIsDisabled_ThenItIsRefused()
    {
        // Arrange
        var executor = CreateExecutor();
        _blinds.IsLocked = true;

        // Act
        var result = await executor.InvokeOperationAsync(Invoke("/Blinds/Open"), CreateScope(), CancellationToken.None);

        // Assert
        Assert.Equal("/Blinds/Open is disabled", result.Message);
        Assert.Equal(0, _blinds.OpenCount);
    }

    [Fact]
    public async Task WhenTheAuthorizerDenies_ThenNothingRuns()
    {
        // Arrange
        var executor = CreateExecutor(new DenyingAuthorizer());

        // Act
        var call = await executor.InvokeOperationAsync(Invoke("/Blinds/Close"), CreateScope(), CancellationToken.None);
        var write = executor.SetProperty(new SetPropertyAction { PropertyPath = "/Blinds/Position", Value = "5" }, CreateScope());

        // Assert
        Assert.Equal("The automation may not call Close", call.Message);
        Assert.Equal("The automation may not write Position", write.Message);
        Assert.Equal(0, _blinds.CloseCount);
        Assert.Equal(0, _blinds.Position);
    }

    [Fact]
    public async Task WhenAnOperationDoesNotEnd_ThenItTimesOutAfterOneMinute()
    {
        // Arrange
        var executor = CreateExecutor();
        var call = executor.InvokeOperationAsync(Invoke("/Blinds/Hang"), CreateScope(), CancellationToken.None);

        // Act
        _time.Advance(ActionExecutor.Timeout);
        var result = await call;

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal("/Blinds/Hang() timed out after 60 seconds", result.Message);
    }

    [Fact]
    public void WhenAPropertyIsWritten_ThenTheValueIsConvertedAndSet()
    {
        // Arrange
        var executor = CreateExecutor();

        // Act
        var result = executor.SetProperty(new SetPropertyAction { PropertyPath = "/Blinds/Position", Value = "/Weather/Gust * 2" }, CreateScope());

        // Assert
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("/Blinds/Position = 18", result.Message);
        Assert.Equal(18, _blinds.Position);
    }

    [Theory]
    [InlineData("/Blinds/Level", "/Blinds/Level is read-only")]
    [InlineData("/Blinds/Label", "/Blinds/Label is configuration, which an automation cannot change")]
    [InlineData("/Blinds/Close", "/Blinds/Close is an operation; call it with an operation action")]
    [InlineData("/Host/Rules[r1]/LastTriggeredAt", "/Host/Rules[r1]/LastTriggeredAt is read-only")]
    [InlineData("/Blinds/Nothing", "No property at /Blinds/Nothing")]
    public void WhenAPropertyCannotBeWritten_ThenTheWriteIsRefused(string path, string expected)
    {
        // Arrange
        var executor = CreateExecutor();

        // Act
        var result = executor.SetProperty(new SetPropertyAction { PropertyPath = path, Value = "1" }, CreateScope());

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(expected, result.Message);
    }

    [Fact]
    public void WhenAnActionIsDescribed_ThenNothingRuns()
    {
        // Arrange
        var executor = CreateExecutor();

        // Act
        var call = executor.Describe(Invoke("/Blinds/MoveTo", new() { ["position"] = "/Weather/Gust" }), CreateScope());
        var write = executor.Describe(new SetPropertyAction { PropertyPath = "/Blinds/Position", Value = "3" }, CreateScope());

        // Assert
        Assert.Equal("/Blinds/MoveTo(position: 9)", call);
        Assert.Equal("/Blinds/Position = 3", write);
        Assert.Equal(0, _blinds.Position);
    }

    private sealed class DenyingAuthorizer : IAutomationAuthorizer
    {
        public string? GetOperationDenial(IInterceptorSubject automation, RegisteredSubjectProperty operation) =>
            $"The automation may not call {operation.Name}";

        public string? GetPropertyWriteDenial(IInterceptorSubject automation, RegisteredSubjectProperty property) =>
            $"The automation may not write {property.Name}";
    }
}
