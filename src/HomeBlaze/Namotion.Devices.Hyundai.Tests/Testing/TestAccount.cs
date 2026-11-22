using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Namotion.Interceptor;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;

namespace Namotion.Devices.Hyundai.Tests.Testing;

/// <summary>An account with a fake handler that answers a full legacy poll, and a fake clock.</summary>
internal static class TestAccount
{
    public static readonly DateTimeOffset Start = new(2026, 11, 22, 10, 30, 0, TimeSpan.Zero);

    public static FakeHttpMessageHandler CreateHandler() => new FakeHttpMessageHandler()
        .Respond(BluelinkResponses.RefreshPath, HttpStatusCode.OK, BluelinkResponses.Tokens(1))
        .Respond(BluelinkResponses.ExchangePath, HttpStatusCode.OK, BluelinkResponses.Ccs(1))
        .Respond(BluelinkResponses.VehiclesPath, HttpStatusCode.OK, FixtureLoader.Load("vehicles.json"))
        .Respond(BluelinkResponses.StatusPath("VEHICLE-1"), HttpStatusCode.OK, FixtureLoader.Load("carstatus-legacy-ev.json"))
        .Respond(BluelinkResponses.StatusPath("VEHICLE-2"), HttpStatusCode.OK, FixtureLoader.Load("carstatus-legacy-ev.json"))
        .Respond(BluelinkResponses.ParkLocationPath("VEHICLE-1"), HttpStatusCode.OK, FixtureLoader.Load("location-park.json"))
        .Respond(BluelinkResponses.ParkLocationPath("VEHICLE-2"), HttpStatusCode.OK, FixtureLoader.Load("location-park.json"));

    public static (BluelinkAccount Account, FakeHttpMessageHandler Handler, FakeTimeProvider Time) Create(
        FakeHttpMessageHandler? handler = null, string password = "", string pin = "", ILoggerFactory? loggerFactory = null)
    {
        handler ??= CreateHandler();
        var time = new FakeTimeProvider(Start);
        var account = new BluelinkAccount(time, loggerFactory ?? NullLoggerFactory.Instance, () => new HttpClient(handler))
        {
            Email = "user@example.invalid",
            Password = password,
            Pin = pin,
            Tokens = BluelinkResponses.TokenSet(0).Serialize(),
            DeviceId = "device-1"
        };
        return (account, handler, time);
    }

    /// <summary>Attaches the account to a configuration writer, as a storage file holds it.</summary>
    public static TestConfigurationWriter AttachWriter(BluelinkAccount account, FakeHttpMessageHandler handler) =>
        new(InterceptorSubjectContext.Create().WithParents()) { Handler = handler, Account = account };

    /// <summary>Advances the fake clock in 5 second steps until the task completes, then awaits it.</summary>
    public static async Task AdvanceUntilCompletedAsync(FakeTimeProvider time, Task task)
    {
        await AsyncTestHelpers.WaitUntilAsync(() =>
        {
            if (!task.IsCompleted)
            {
                time.Advance(TimeSpan.FromSeconds(5));
            }

            return task.IsCompleted;
        });
        await task;
    }

    /// <summary>A response that answers once the test completes it.</summary>
    public static TaskCompletionSource<HttpResponseMessage> CreateHeldResponse() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static HttpResponseMessage CreateResponse(HttpStatusCode statusCode, string body) =>
        new(statusCode) { Content = new StringContent(body) };
}
