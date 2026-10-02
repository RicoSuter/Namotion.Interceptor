using System.Net;
using System.Reactive.Concurrency;
using System.Text;
using HomeBlaze.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Xunit;

namespace Namotion.Devices.MyStrom.Tests;

public class MyStromPollerTests
{
    [Fact]
    public async Task WhenStarted_ThenStateIsPolled()
    {
        // Arrange
        var handler = new StubMyStromHandler();
        await using var provider = CreateProvider(handler);
        var subject = new MyStromSwitch { HostAddress = "192.168.1.59" };

        // Act
        await using var running = await subject.StartAsync(provider);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => subject.IsConnected && subject.Temperature == 21.5m);
        Assert.True(subject.IsOn);
        Assert.Equal(12.3m, subject.MeasuredPower);
        Assert.Equal("AABBCCDDEEFF", subject.MacAddress);
    }

    [Fact]
    public async Task WhenStartedAndTurnedOff_ThenRelayRequestIsSent()
    {
        // Arrange
        var handler = new StubMyStromHandler();
        await using var provider = CreateProvider(handler);
        var subject = new MyStromSwitch { HostAddress = "192.168.1.59" };
        await using var running = await subject.StartAsync(provider);
        await AsyncTestHelpers.WaitUntilAsync(() => subject.IsConnected);

        // Act
        await subject.TurnOffAsync(CancellationToken.None);

        // Assert
        Assert.Contains("/relay?state=0", handler.RequestedPaths);
    }

    [Fact]
    public async Task WhenStopped_ThenOperationsThrowAgain()
    {
        // Arrange
        var handler = new StubMyStromHandler();
        await using var provider = CreateProvider(handler);
        var subject = new MyStromSwitch { HostAddress = "192.168.1.59" };
        var running = await subject.StartAsync(provider);
        await AsyncTestHelpers.WaitUntilAsync(() => subject.IsConnected);

        // Act
        await running.DisposeAsync();

        // Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => subject.TurnOnAsync(CancellationToken.None));
    }

    [Fact]
    public async Task WhenStopped_ThenOperationsAreNoLongerEnabled()
    {
        // Arrange
        var handler = new StubMyStromHandler();
        await using var provider = CreateProvider(handler);
        var subject = new MyStromSwitch { HostAddress = "192.168.1.59" };
        var running = await subject.StartAsync(provider);
        await AsyncTestHelpers.WaitUntilAsync(() => subject.IsConnected && subject.IsOn == true);

        // Act
        await running.DisposeAsync();

        // Assert
        Assert.Equal(ServiceStatus.Stopped, subject.Status);
        Assert.False(subject.IsConnected);
        Assert.False(subject.TurnOn_IsEnabled);
        Assert.False(subject.TurnOff_IsEnabled);
    }

    [Fact]
    public async Task WhenPollFails_ThenStatusIsErrorAndPollerReconnects()
    {
        // Arrange
        var firstReport = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubMyStromHandler(failingReportGate: firstReport.Task);
        await using var provider = CreateProvider(handler);
        var subject = new MyStromSwitch { HostAddress = "192.168.1.59" };
        await using var running = await subject.StartAsync(provider);

        // The poller reconnects right after a failed poll, so Error is transient and is recorded
        // synchronously from the change stream rather than sampled.
        var changes = new List<(string Name, object? Value)>();
        using var subscription = ((IInterceptorSubject)subject).Context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(change =>
            {
                if (change.Property.Name is nameof(MyStromSwitch.Status) or nameof(MyStromSwitch.IsConnected))
                {
                    lock (changes)
                    {
                        changes.Add((change.Property.Name, change.GetNewValue<object?>()));
                    }
                }
            });

        // Act
        firstReport.SetResult();

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => handler.InformationRequestCount >= 2 && subject.IsConnected && subject.Status == ServiceStatus.Running);
        (string Name, object? Value)[] recorded;
        lock (changes)
        {
            recorded = changes.ToArray();
        }

        var errorIndex = Array.IndexOf(recorded, (nameof(MyStromSwitch.Status), (object?)ServiceStatus.Error));
        Assert.True(errorIndex >= 0, "Status never became Error.");
        Assert.Contains((nameof(MyStromSwitch.IsConnected), (object?)false), recorded[..errorIndex]);
        Assert.Contains((nameof(MyStromSwitch.IsConnected), (object?)true), recorded[(errorIndex + 1)..]);
    }

    [Fact]
    public async Task WhenStoppedWhileWaitingToRetryAFailedConnect_ThenStatusIsStopped()
    {
        // Arrange
        var handler = new StubMyStromHandler(failInformation: true);
        await using var provider = CreateProvider(handler);
        var subject = new MyStromSwitch { HostAddress = "192.168.1.59", RetryInterval = TimeSpan.FromHours(1) };
        var running = await subject.StartAsync(provider);

        // Error is set right before the retry wait, and the long interval keeps the poller in it.
        await AsyncTestHelpers.WaitUntilAsync(() =>
            handler.InformationRequestCount == 1 && subject.Status == ServiceStatus.Error);

        // Act
        await running.DisposeAsync();

        // Assert
        Assert.Equal(ServiceStatus.Stopped, subject.Status);
        Assert.False(subject.IsConnected);
        Assert.Equal(1, handler.InformationRequestCount);
    }

    private static ServiceProvider CreateProvider(HttpMessageHandler handler)
        => new ServiceCollection()
            .AddSingleton<IHttpClientFactory>(new StubHttpClientFactory(handler))
            .BuildServiceProvider();

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>
    /// Answers like a myStrom switch. With a gate, the first /report waits for the gate and then fails
    /// with 500, every later /report succeeds. With <paramref name="failInformation"/>, every /info
    /// fails with 500.
    /// </summary>
    private sealed class StubMyStromHandler(Task? failingReportGate = null, bool failInformation = false) : HttpMessageHandler
    {
        private readonly List<string> _requestedPaths = [];
        private int _reportRequestCount;
        private int _informationRequestCount;

        public IReadOnlyList<string> RequestedPaths
        {
            get { lock (_requestedPaths) { return _requestedPaths.ToArray(); } }
        }

        public int InformationRequestCount => Volatile.Read(ref _informationRequestCount);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var pathAndQuery = request.RequestUri!.PathAndQuery;
            lock (_requestedPaths)
            {
                _requestedPaths.Add(pathAndQuery);
            }

            if (request.RequestUri.AbsolutePath == "/info")
            {
                Interlocked.Increment(ref _informationRequestCount);
                if (failInformation)
                {
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                }
            }

            if (failingReportGate is not null &&
                request.RequestUri.AbsolutePath == "/report" &&
                Interlocked.Increment(ref _reportRequestCount) == 1)
            {
                await failingReportGate.WaitAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }

            var json = request.RequestUri.AbsolutePath switch
            {
                "/info" => """{"version":"3.82.60","mac":"AABBCCDDEEFF","ip":"192.168.1.59","mask":"255.255.255.0","gw":"192.168.1.1","type":"107"}""",
                "/report" => """{"power":12.34,"Ws":12.1,"relay":true,"temperature":24.9,"energy_since_boot":7200,"time_since_boot":60}""",
                "/api/v1/temperature" => """{"measured":25.0,"compensation":3.5,"compensated":21.5}""",
                "/relay" => "{}",
                _ => throw new InvalidOperationException($"Unexpected request {pathAndQuery}")
            };

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }
}
