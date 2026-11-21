using System.Net;
using System.Text.Json.Nodes;
using HomeBlaze.Abstractions;
using HomeBlaze.Storage.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Namotion.Devices.Tesla.Client;
using Namotion.Devices.Tesla.Tests.Testing;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;

namespace Namotion.Devices.Tesla.Tests;

public class TeslaAccountTests
{
    private const string TokenPath = "/oauth2/v3/token";
    private const string ProductsPath = "/api/1/products";
    private const string VehicleDataPath = "/api/1/vehicles/1000000000000001/vehicle_data";
    private const string SecondVehicleDataPath = "/api/1/vehicles/1000000000000004/vehicle_data";
    private const string WakeUpPath = "/api/1/vehicles/1000000000000001/wake_up";
    private const string TokenJson = """{"access_token":"access-1","refresh_token":"refresh-2","expires_in":28800}""";

    private static readonly DateTimeOffset Start = DateTimeOffset.FromUnixTimeMilliseconds(1795293700000);

    private static (TeslaAccount Account, FakeHttpMessageHandler Handler, FakeTimeProvider Time) Create(FakeHttpMessageHandler? handler = null)
    {
        handler ??= new FakeHttpMessageHandler()
            .Respond(TokenPath, HttpStatusCode.OK, TokenJson)
            .Respond(ProductsPath, HttpStatusCode.OK, FixtureLoader.Load("products.json"))
            .Respond(VehicleDataPath, HttpStatusCode.OK, FixtureLoader.Load("vehicle-data.json"));
        var time = new FakeTimeProvider(Start);
        var account = new TeslaAccount(time, NullLoggerFactory.Instance, () => new HttpClient(handler)) { RefreshToken = "refresh-1" };
        return (account, handler, time);
    }

    private static int CountRequests(FakeHttpMessageHandler handler, string path) =>
        handler.Requests.Count(request => request.PathAndQuery.StartsWith(path, StringComparison.Ordinal));

    private static bool AdvanceUntil(FakeTimeProvider time, bool condition, int stepSeconds = 1)
    {
        if (!condition)
        {
            time.Advance(TimeSpan.FromSeconds(stepSeconds));
        }

        return condition;
    }

    /// <summary>The products fixture with a second, online vehicle without cached data.</summary>
    private static string ProductsWithTwoVehicles()
    {
        var products = JsonNode.Parse(FixtureLoader.Load("products.json"))!;
        products["response"]!.AsArray().Add(new JsonObject
        {
            ["id"] = 1000000000000004,
            ["vehicle_id"] = 2000000000000005,
            ["vin"] = "TESTVIN0000000002",
            ["state"] = "online"
        });
        return products.ToJsonString();
    }

    [Fact]
    public async Task WhenPolledOnce_ThenVehiclesAreDiscoveredAndRead()
    {
        // Arrange
        var (account, _, _) = Create();

        // Act
        await account.PollOnceAsync(CancellationToken.None);

        // Assert
        var vehicle = Assert.Single(account.Vehicles).Value;
        Assert.Equal("TESTVIN0000000001", Assert.Single(account.Vehicles).Key);
        Assert.Equal(0.7m, vehicle.BatteryLevel);
        Assert.True(account.IsAuthenticated);
        Assert.Equal(Start, account.AuthenticatedAt);
        Assert.Equal("refresh-2", account.RefreshToken);
        Assert.Equal(ServiceStatus.Running, account.Status);
        Assert.Null(account.StatusMessage);
    }

    [Fact]
    public async Task WhenPolledAgain_ThenVehicleInstanceIsKept()
    {
        // Arrange
        var (account, _, time) = Create();
        await account.PollOnceAsync(CancellationToken.None);
        var vehicle = account.Vehicles.Single().Value;
        var vehicles = account.Vehicles;

        // Act
        time.Advance(TimeSpan.FromMinutes(1));
        await account.PollOnceAsync(CancellationToken.None);

        // Assert
        Assert.Same(vehicle, account.Vehicles.Single().Value);
        Assert.Same(vehicles, account.Vehicles);
    }

    [Fact]
    public async Task WhenVehicleIsAsleep_ThenVehicleDataIsNotRequested()
    {
        // Arrange
        var (account, handler, time) = Create();

        // The handler repeats the last queued response: the first poll gets the online fixture, every later poll the asleep one.
        handler.Respond(ProductsPath, HttpStatusCode.OK, FixtureLoader.Load("products.json").Replace("\"online\"", "\"asleep\""));
        await account.PollOnceAsync(CancellationToken.None);
        var readsAfterFirstPoll = CountRequests(handler, VehicleDataPath);

        // Act
        time.Advance(TimeSpan.FromMinutes(10));
        await account.PollOnceAsync(CancellationToken.None);

        // Assert
        Assert.Equal(1, readsAfterFirstPoll);
        Assert.Equal(readsAfterFirstPoll, CountRequests(handler, VehicleDataPath));
        Assert.Equal(TeslaVehicleState.Asleep, account.Vehicles.Single().Value.VehicleState);
        Assert.Equal(0, CountRequests(handler, WakeUpPath));
    }

    [Fact]
    public async Task WhenVehicleFallsAsleepBeforeItIsRead_ThenItIsMarkedAsleep()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler()
            .Respond(TokenPath, HttpStatusCode.OK, TokenJson)
            .Respond(ProductsPath, HttpStatusCode.OK, FixtureLoader.Load("products.json"))
            .Respond(VehicleDataPath, HttpStatusCode.RequestTimeout, """{"error":"vehicle unavailable"}""");
        var (account, _, _) = Create(handler);

        // Act
        await account.PollOnceAsync(CancellationToken.None);

        // Assert
        Assert.Equal(TeslaVehicleState.Asleep, account.Vehicles.Single().Value.VehicleState);
        Assert.Equal(ServiceStatus.Running, account.Status);
        Assert.Equal(0, CountRequests(handler, WakeUpPath));
    }

    [Fact]
    public async Task WhenVehicleDisappears_ThenItIsRemoved()
    {
        // Arrange
        var (account, handler, _) = Create();
        handler.Respond(ProductsPath, HttpStatusCode.OK, """{"response":[]}""");
        await account.PollOnceAsync(CancellationToken.None);

        // Act
        await account.PollOnceAsync(CancellationToken.None);

        // Assert
        Assert.Empty(account.Vehicles);
    }

    [Fact]
    public async Task WhenReadingOneVehicleFails_ThenTheOtherVehiclesAreStillRead()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler()
            .Respond(TokenPath, HttpStatusCode.OK, TokenJson)
            .Respond(ProductsPath, HttpStatusCode.OK, ProductsWithTwoVehicles())
            .Respond(VehicleDataPath, HttpStatusCode.InternalServerError, """{"error":"internal"}""")
            .Respond(SecondVehicleDataPath, HttpStatusCode.OK, FixtureLoader.Load("vehicle-data.json"));
        var (account, _, _) = Create(handler);

        // Act
        await account.PollOnceAsync(CancellationToken.None);

        // Assert
        Assert.Equal(2, account.Vehicles.Count);
        Assert.Equal(0.7m, account.Vehicles["TESTVIN0000000002"].BatteryLevel);
        Assert.Equal(1, CountRequests(handler, VehicleDataPath));
        Assert.Equal(ServiceStatus.Running, account.Status);
        Assert.Contains("500", account.StatusMessage);
    }

    [Fact]
    public async Task WhenReadingVehicleReturnsInvalidJson_ThenTheOtherVehiclesAreStillRead()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler()
            .Respond(TokenPath, HttpStatusCode.OK, TokenJson)
            .Respond(ProductsPath, HttpStatusCode.OK, ProductsWithTwoVehicles())
            .Respond(VehicleDataPath, HttpStatusCode.OK, "{not json")
            .Respond(SecondVehicleDataPath, HttpStatusCode.OK, FixtureLoader.Load("vehicle-data.json"));
        var (account, _, _) = Create(handler);

        // Act
        await account.PollOnceAsync(CancellationToken.None);

        // Assert
        Assert.Equal(0.7m, account.Vehicles["TESTVIN0000000002"].BatteryLevel);
        Assert.Equal(ServiceStatus.Running, account.Status);
        Assert.NotNull(account.StatusMessage);
    }

    [Fact]
    public async Task WhenRefreshIsRejected_ThenReauthenticationIsShownAndTheLoopWaitsForConfiguration()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler().Respond(TokenPath, HttpStatusCode.Unauthorized, """{"error":"login_required"}""");
        var (account, _, _) = Create(handler);

        // Act
        await account.PollOnceAsync(CancellationToken.None);

        // Assert
        Assert.False(account.IsAuthenticated);
        Assert.Equal(ServiceStatus.Error, account.Status);
        Assert.Equal("Re-authentication required", account.StatusMessage);
        Assert.True(account.IsReauthenticationRequired);
        Assert.Equal(Timeout.InfiniteTimeSpan, account.GetNextPollDelay());
    }

    [Fact]
    public async Task WhenFirstPollFailsWithANetworkError_ThenReauthenticationIsNotRequired()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler()
            .Respond(TokenPath, () => Task.FromException<HttpResponseMessage>(new HttpRequestException("Network unreachable")));
        var (account, _, _) = Create(handler);

        // Act
        await account.PollOnceAsync(CancellationToken.None);

        // Assert
        Assert.Equal(ServiceStatus.Error, account.Status);
        Assert.False(account.IsAuthenticated);
        Assert.False(account.IsReauthenticationRequired);
    }

    [Fact]
    public async Task WhenRejectedRefreshTokenIsReplaced_ThenReauthenticationIsNoLongerRequired()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler()
            .Respond(TokenPath, HttpStatusCode.Unauthorized, """{"error":"login_required"}""")
            .Respond(TokenPath, HttpStatusCode.OK, TokenJson)
            .Respond(ProductsPath, HttpStatusCode.OK, FixtureLoader.Load("products.json"))
            .Respond(VehicleDataPath, HttpStatusCode.OK, FixtureLoader.Load("vehicle-data.json"));
        var (account, _, _) = Create(handler);
        await account.PollOnceAsync(CancellationToken.None);
        var isRequiredBeforeReplacement = account.IsReauthenticationRequired;

        // Act
        account.ReplaceRefreshToken("refresh-new");
        await account.ApplyConfigurationAsync(CancellationToken.None);
        await account.PollOnceAsync(CancellationToken.None);

        // Assert
        Assert.True(isRequiredBeforeReplacement);
        Assert.False(account.IsReauthenticationRequired);
        Assert.True(account.IsAuthenticated);
        Assert.Equal(ServiceStatus.Running, account.Status);
    }

    [Fact]
    public async Task WhenRequestsAreLimited_ThenPollingPausesForRetryAfter()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler()
            .Respond(TokenPath, HttpStatusCode.OK, TokenJson)
            .Respond(ProductsPath, HttpStatusCode.TooManyRequests, """{"error":"too many requests"}""", ("Retry-After", "600"));
        var (account, _, _) = Create(handler);

        // Act
        await account.PollOnceAsync(CancellationToken.None);

        // Assert
        Assert.Equal(ServiceStatus.Error, account.Status);
        Assert.Equal(TimeSpan.FromMinutes(10), account.GetNextPollDelay());
    }

    [Fact]
    public async Task WhenAccountExceedsItsLimit_ThenPollingPausesForFifteenMinutes()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler()
            .Respond(TokenPath, HttpStatusCode.OK, TokenJson)
            .Respond(ProductsPath, HttpStatusCode.Forbidden, """{"error":"account disabled: EXCEEDED_LIMIT"}""");
        var (account, _, _) = Create(handler);

        // Act
        await account.PollOnceAsync(CancellationToken.None);

        // Assert
        Assert.Equal(ServiceStatus.Error, account.Status);
        Assert.Equal(TimeSpan.FromMinutes(15), account.GetNextPollDelay());
    }

    [Fact]
    public async Task WhenReadingVehicleIsLimited_ThenThePollStopsAndPauses()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler()
            .Respond(TokenPath, HttpStatusCode.OK, TokenJson)
            .Respond(ProductsPath, HttpStatusCode.OK, FixtureLoader.Load("products.json"))
            .Respond(VehicleDataPath, HttpStatusCode.TooManyRequests, """{"error":"too many requests"}""", ("Retry-After", "300"));
        var (account, _, _) = Create(handler);

        // Act
        await account.PollOnceAsync(CancellationToken.None);

        // Assert
        Assert.Equal(ServiceStatus.Error, account.Status);
        Assert.Equal(TimeSpan.FromMinutes(5), account.GetNextPollDelay());
    }

    [Fact]
    public async Task WhenServerFails_ThenPollingRetriesAfterRetryInterval()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler()
            .Respond(TokenPath, HttpStatusCode.OK, TokenJson)
            .Respond(ProductsPath, HttpStatusCode.BadGateway, """{"error":"bad gateway"}""");
        var (account, _, _) = Create(handler);
        account.RetryInterval = TimeSpan.FromMinutes(2);

        // Act
        await account.PollOnceAsync(CancellationToken.None);

        // Assert
        // Not yet authenticated, but only a rejected refresh token waits for a configuration change.
        Assert.Equal(ServiceStatus.Error, account.Status);
        Assert.False(account.IsAuthenticated);
        Assert.Equal(TimeSpan.FromMinutes(2), account.GetNextPollDelay());
    }

    [Fact]
    public async Task WhenPollSucceeds_ThenTheNextPollFollowsTheStatePollingInterval()
    {
        // Arrange
        var (account, _, _) = Create();
        account.StatePollingInterval = TimeSpan.FromMinutes(3);

        // Act
        await account.PollOnceAsync(CancellationToken.None);

        // Assert
        Assert.Equal(TimeSpan.FromMinutes(3), account.GetNextPollDelay());
    }

    [Fact]
    public async Task WhenPolledAgain_ThenTheTokenManagerIsKeptAndTheTokenIsNotRefreshedAgain()
    {
        // Arrange
        var (account, handler, time) = Create();
        await account.PollOnceAsync(CancellationToken.None);

        // Act
        time.Advance(TimeSpan.FromMinutes(1));
        await account.ApplyConfigurationAsync(CancellationToken.None);
        await account.PollOnceAsync(CancellationToken.None);

        // Assert
        Assert.Equal(1, CountRequests(handler, TokenPath));
    }

    [Fact]
    public async Task WhenRefreshTokenIsReplaced_ThenTheNewTokenIsUsed()
    {
        // Arrange
        var (account, handler, _) = Create();
        await account.PollOnceAsync(CancellationToken.None);

        // Act
        account.RefreshToken = "refresh-new";
        await account.ApplyConfigurationAsync(CancellationToken.None);
        await account.PollOnceAsync(CancellationToken.None);

        // Assert
        var tokenRequests = handler.Requests.Where(request => request.PathAndQuery == TokenPath).ToArray();
        Assert.Equal(2, tokenRequests.Length);
        Assert.Contains("refresh_token=refresh-new", tokenRequests[1].Body);
    }

    [Fact]
    public async Task WhenTokenIsRotated_ThenItIsWrittenThroughTheConfigurationWriterBeforeUse()
    {
        // Arrange
        var (account, handler, _) = Create();
        var writer = new TestConfigurationWriter(InterceptorSubjectContext.Create().WithParents()) { Handler = handler, Account = account };

        // Act
        await account.PollOnceAsync(CancellationToken.None);

        // Assert
        var write = Assert.Single(writer.Writes);
        Assert.Equal("refresh-2", write.RefreshToken);
        Assert.Equal(1, write.RequestCount);
        Assert.Equal(ServiceStatus.Running, account.Status);
    }

    [Fact]
    public async Task WhenWritingTheRotatedTokenFails_ThenThePollFailsAndOnlyTheWriteIsRetried()
    {
        // Arrange
        var (account, handler, _) = Create();
        var writer = new TestConfigurationWriter(InterceptorSubjectContext.Create().WithParents()) { Handler = handler, Account = account, ShouldFail = true };
        await account.PollOnceAsync(CancellationToken.None);
        var statusAfterFailure = account.Status;
        var productRequestsAfterFailure = CountRequests(handler, ProductsPath);

        // Act
        writer.ShouldFail = false;
        await account.PollOnceAsync(CancellationToken.None);

        // Assert
        Assert.Equal(ServiceStatus.Error, statusAfterFailure);
        Assert.Equal(0, productRequestsAfterFailure);
        Assert.Equal(1, CountRequests(handler, TokenPath));
        Assert.Equal(["refresh-2", "refresh-2"], writer.Writes.Select(write => write.RefreshToken));
        Assert.Equal(ServiceStatus.Running, account.Status);
        Assert.Equal("refresh-2", account.RefreshToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenRefreshTokenIsReplacedDuringARotation_ThenTheRotationDoesNotOverwriteIt(bool useReplaceRefreshToken)
    {
        // Arrange
        var tokenResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeHttpMessageHandler()
            .Respond(TokenPath, () => tokenResponse.Task)
            .Respond(TokenPath, HttpStatusCode.OK, """{"access_token":"access-3","refresh_token":"refresh-4","expires_in":28800}""")
            .Respond(ProductsPath, HttpStatusCode.OK, FixtureLoader.Load("products.json"))
            .Respond(VehicleDataPath, HttpStatusCode.OK, FixtureLoader.Load("vehicle-data.json"));
        var (account, _, _) = Create(handler);
        var writer = new TestConfigurationWriter(InterceptorSubjectContext.Create().WithParents()) { Handler = handler, Account = account };
        var poll = account.PollOnceAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => CountRequests(handler, TokenPath) == 1);

        // Act
        if (useReplaceRefreshToken)
        {
            account.ReplaceRefreshToken("refresh-new");
        }
        else
        {
            account.RefreshToken = "refresh-new";
        }

        tokenResponse.SetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(TokenJson) });
        await poll;
        var refreshTokenAfterRotation = account.RefreshToken;
        var writesAfterRotation = writer.Writes.Count;
        await account.PollOnceAsync(CancellationToken.None);

        // Assert
        Assert.Equal("refresh-new", refreshTokenAfterRotation);
        Assert.Equal(0, writesAfterRotation);
        var tokenRequests = handler.Requests.Where(request => request.PathAndQuery == TokenPath).ToArray();
        Assert.Equal(2, tokenRequests.Length);
        Assert.Contains("refresh_token=refresh-new", tokenRequests[1].Body);
        Assert.Equal(["refresh-4"], writer.Writes.Select(write => write.RefreshToken));
        Assert.Equal("refresh-4", account.RefreshToken);
    }

    [Fact]
    public async Task WhenPollRunsDuringWakeUp_ThenItWaitsUntilTheWakeUpCompletes()
    {
        // Arrange
        var (account, handler, _) = Create();
        await account.PollOnceAsync(CancellationToken.None);
        var wakeUpResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.Respond(WakeUpPath, () => wakeUpResponse.Task);
        var wakeUp = account.Vehicles.Single().Value.WakeUpAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => CountRequests(handler, WakeUpPath) == 1);

        // Act
        var poll = account.PollOnceAsync(CancellationToken.None);
        var isPollCompletedDuringWakeUp = poll.IsCompleted;
        var productRequestsDuringWakeUp = CountRequests(handler, ProductsPath);
        wakeUpResponse.SetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"response":{"state":"online"}}""") });
        await wakeUp;
        await poll;

        // Assert
        Assert.False(isPollCompletedDuringWakeUp);
        Assert.Equal(1, productRequestsDuringWakeUp);
        Assert.Equal(2, CountRequests(handler, ProductsPath));
        Assert.Equal(ServiceStatus.Running, account.Status);
    }

    [Fact]
    public async Task WhenWakeUpIsInvoked_ThenWakeUpIsCalledOnlyThen()
    {
        // Arrange
        var (account, handler, _) = Create();
        handler.Respond(WakeUpPath, HttpStatusCode.OK, """{"response":{"state":"online"}}""");
        await account.PollOnceAsync(CancellationToken.None);
        var wakeUpsBeforeOperation = CountRequests(handler, WakeUpPath);

        // Act
        await account.Vehicles.Single().Value.WakeUpAsync(CancellationToken.None);

        // Assert
        Assert.Equal(0, wakeUpsBeforeOperation);
        Assert.Equal(1, CountRequests(handler, WakeUpPath));
    }

    [Fact]
    public async Task WhenWakeUpIsRejected_ThenReauthenticationIsRequired()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler()
            .Respond(TokenPath, HttpStatusCode.OK, TokenJson)
            .Respond(TokenPath, HttpStatusCode.Unauthorized, """{"error":"login_required"}""")
            .Respond(ProductsPath, HttpStatusCode.OK, FixtureLoader.Load("products.json").Replace("\"online\"", "\"asleep\""))
            .Respond(WakeUpPath, HttpStatusCode.Unauthorized, """{"error":"invalid bearer token"}""");
        var (account, _, _) = Create(handler);
        await account.PollOnceAsync(CancellationToken.None);

        // Act & Assert
        await Assert.ThrowsAsync<TeslaReauthenticationRequiredException>(
            () => account.Vehicles.Single().Value.WakeUpAsync(CancellationToken.None));
        Assert.True(account.IsReauthenticationRequired);
        Assert.False(account.IsAuthenticated);
        Assert.Equal(ServiceStatus.Error, account.Status);
        Assert.Equal(Timeout.InfiniteTimeSpan, account.GetNextPollDelay());
    }

    [Fact]
    public async Task WhenWakingVehicleTakesTime_ThenItIsReadOnceOnline()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler()
            .Respond(TokenPath, HttpStatusCode.OK, TokenJson)
            .Respond(ProductsPath, HttpStatusCode.OK, FixtureLoader.Load("products.json").Replace("\"online\"", "\"asleep\""))
            .Respond(WakeUpPath, HttpStatusCode.OK, """{"response":{"state":"asleep"}}""")
            .Respond(VehicleDataPath, HttpStatusCode.RequestTimeout, """{"error":"vehicle unavailable"}""")
            .Respond(VehicleDataPath, HttpStatusCode.OK, FixtureLoader.Load("vehicle-data.json"));
        var (account, _, time) = Create(handler);
        await account.PollOnceAsync(CancellationToken.None);
        var vehicle = account.Vehicles.Single().Value;

        // Act
        var wakeUp = vehicle.WakeUpAsync(CancellationToken.None);

        // Advances in small steps until the wait between the checks has passed, also when the timer starts late.
        await AsyncTestHelpers.WaitUntilAsync(() => AdvanceUntil(time, wakeUp.IsCompleted));
        await wakeUp;

        // Assert
        Assert.Equal(TeslaVehicleState.Online, vehicle.VehicleState);
        Assert.Equal(0.7m, vehicle.BatteryLevel);
        Assert.Equal(1, CountRequests(handler, WakeUpPath));
    }

    [Fact]
    public async Task WhenStartedWithoutRefreshToken_ThenItWaitsForTheConfiguration()
    {
        // Arrange
        var (account, handler, _) = Create();
        account.RefreshToken = string.Empty;
        using var stopping = new CancellationTokenSource();

        await account.StartAsync(stopping.Token);
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(() => account.StatusMessage == "Missing refresh token");

            // Act
            account.RefreshToken = "refresh-1";
            await account.ApplyConfigurationAsync(CancellationToken.None);

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(() => account.Status == ServiceStatus.Running);
            Assert.Single(account.Vehicles);
            Assert.Equal(1, CountRequests(handler, ProductsPath));
        }
        finally
        {
            await account.StopAsync(CancellationToken.None);
            account.Dispose();
        }

        Assert.Equal(ServiceStatus.Stopped, account.Status);
    }

    [Fact]
    public async Task WhenLoopRuns_ThenItPollsAgainAfterTheStatePollingInterval()
    {
        // Arrange
        var (account, handler, time) = Create();
        await account.StartAsync(CancellationToken.None);
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(() => account.Status == ServiceStatus.Running);

            // Act & Assert
            await AsyncTestHelpers.WaitUntilAsync(() => AdvanceUntil(time, CountRequests(handler, ProductsPath) >= 2, stepSeconds: 15));
        }
        finally
        {
            await account.StopAsync(CancellationToken.None);
            account.Dispose();
        }
    }
}

[InterceptorSubject]
public partial class TestConfigurationWriter : IConfigurationWriter
{
    private readonly List<(string RefreshToken, int RequestCount)> _writes = [];

    public partial TeslaAccount? Account { get; set; }

    internal FakeHttpMessageHandler? Handler { get; init; }

    public bool ShouldFail { get; set; }

    /// <summary>The refresh token of each write with the number of HTTP requests sent before it.</summary>
    internal IReadOnlyList<(string RefreshToken, int RequestCount)> Writes => _writes;

    public Task<bool> WriteConfigurationAsync(IInterceptorSubject subject, CancellationToken cancellationToken)
    {
        _writes.Add((((TeslaAccount)subject).RefreshToken, Handler?.Requests.Count ?? 0));
        return ShouldFail ? throw new IOException("The disk is full.") : Task.FromResult(true);
    }
}
