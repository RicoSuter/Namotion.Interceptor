using HomeBlaze.Automation.Expressions;
using HomeBlaze.Automation.Runtime;
using HomeBlaze.Automation.Tests.Testing;
using Microsoft.Extensions.Time.Testing;

namespace HomeBlaze.Automation.Tests.Runtime;

public class ActionExecutorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    private readonly RuleGraph _graph = new();
    private readonly FakeTimeProvider _time = new(Now);

    private ActionExecutor CreateExecutor(IAutomationAuthorizer? authorizer = null) =>
        new(_graph.Resolver, authorizer ?? new AllowAllAutomationAuthorizer(), serviceProvider: null, _time);

    private ActionScope CreateScope() =>
        new(_graph.Host, _graph.Rule, new AutomationEvaluation(_graph.Resolver, _graph.Rule, Now, TimeZoneInfo.Utc), AllowsEvent: false);

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
        Assert.Equal(40, _graph.Blinds.Position);
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
        Assert.Equal("Gust 9 m/s", Assert.Single(_graph.Notifier.Sent).Message);
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
        _graph.Blinds.IsLocked = true;

        // Act
        var result = await executor.InvokeOperationAsync(Invoke("/Blinds/Open"), CreateScope(), CancellationToken.None);

        // Assert
        Assert.Equal("/Blinds/Open is disabled", result.Message);
        Assert.Equal(0, _graph.Blinds.OpenCount);
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
        Assert.Equal(0, _graph.Blinds.CloseCount);
        Assert.Equal(0, _graph.Blinds.Position);
    }

    [Fact]
    public async Task WhenTheAuthorizerThrows_ThenTheActionIsRefusedWithTheReason()
    {
        // Arrange
        var executor = CreateExecutor(new ThrowingAuthorizer());
        var write = new SetPropertyAction { PropertyPath = "/Blinds/Position", Value = "5" };

        // Act
        var call = await executor.InvokeOperationAsync(Invoke("/Blinds/Close"), CreateScope(), CancellationToken.None);
        var written = executor.SetProperty(write, CreateScope());
        var described = executor.Describe(write, CreateScope());

        // Assert
        Assert.False(call.IsSuccess);
        Assert.Equal("/Blinds/Close: the authorization failed: The policy store is offline", call.Message);
        Assert.Equal("/Blinds/Position: the authorization failed: The policy store is offline", written.Message);
        Assert.Equal("/Blinds/Position: the authorization failed: The policy store is offline", described);
        Assert.Equal(0, _graph.Blinds.CloseCount);
        Assert.Equal(0, _graph.Blinds.Position);
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
        Assert.Equal(18, _graph.Blinds.Position);
    }

    [Fact]
    public void WhenANullablePropertyIsWrittenWithAnEmptyValue_ThenItIsCleared()
    {
        // Arrange
        var executor = CreateExecutor();
        _graph.Blinds.Tilt = 0.5;

        // Act
        var result = executor.SetProperty(new SetPropertyAction { PropertyPath = "/Blinds/Tilt", Value = "" }, CreateScope());

        // Assert
        Assert.True(result.IsSuccess, result.Message);
        Assert.Null(_graph.Blinds.Tilt);
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
        Assert.Equal(0, _graph.Blinds.Position);
    }
}
