using System.Collections.Concurrent;
using System.Net;
using System.Text;
using HomeBlaze.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Testing;
using Xunit;

namespace Namotion.Devices.MyStrom.Tests;

public class MyStromSwitchTests
{
    private static MyStromSwitch CreateSwitch()
    {
        var httpClientFactory = new TestHttpClientFactory();
        return new MyStromSwitch(httpClientFactory, NullLogger<MyStromSwitch>.Instance);
    }

    [Fact]
    public void WhenCreated_ThenHasDefaultValues()
    {
        // Act
        var subject = CreateSwitch();

        // Assert
        Assert.Equal(string.Empty, subject.Name);
        Assert.Null(subject.HostAddress);
        Assert.True(subject.AllowTurnOff);
        Assert.Equal(TimeSpan.FromSeconds(15), subject.PollingInterval);
        Assert.Equal(TimeSpan.FromSeconds(30), subject.RetryInterval);
    }

    [Fact]
    public void WhenCreated_ThenStatusIsStopped()
    {
        // Act
        var subject = CreateSwitch();

        // Assert
        Assert.Equal(ServiceStatus.Stopped, subject.Status);
        Assert.Null(subject.StatusMessage);
        Assert.False(subject.IsConnected);
    }

    [Fact]
    public void WhenCreated_ThenStatePropertiesAreNull()
    {
        // Act
        var subject = CreateSwitch();

        // Assert
        Assert.Null(subject.IsOn);
        Assert.Null(subject.MeasuredPower);
        Assert.Null(subject.TotalImportedEnergy);
        Assert.Null(subject.TotalExportedEnergy);
        Assert.Null(subject.Temperature);
        Assert.Null(subject.Uptime);
        Assert.Null(subject.LastUpdated);
    }

    [Fact]
    public void WhenNameIsSet_ThenTitleReturnsName()
    {
        // Arrange
        var subject = CreateSwitch();

        // Act
        subject.Name = "Kitchen Switch";

        // Assert
        Assert.Equal("Kitchen Switch", subject.Title);
    }

    [Fact]
    public void WhenNameIsEmpty_ThenTitleFallsBackToHostAddress()
    {
        // Arrange
        var subject = CreateSwitch();

        // Act
        subject.HostAddress = "192.168.1.59";

        // Assert
        Assert.Equal("192.168.1.59", subject.Title);
    }

    [Fact]
    public void WhenIconName_ThenReturnsPower()
    {
        // Arrange
        var subject = CreateSwitch();

        // Assert
        Assert.Equal("Power", subject.IconName);
    }

    [Fact]
    public void WhenIsOnTrue_ThenIconColorIsSuccess()
    {
        // Arrange
        var subject = CreateSwitch();

        // Act
        subject.IsOn = true;

        // Assert
        Assert.Equal("Success", subject.IconColor);
    }

    [Fact]
    public void WhenIsOnFalse_ThenIconColorIsError()
    {
        // Arrange
        var subject = CreateSwitch();

        // Act
        subject.IsOn = false;

        // Assert
        Assert.Equal("Error", subject.IconColor);
    }

    [Fact]
    public void WhenIsOnNull_ThenIconColorIsNull()
    {
        // Arrange
        var subject = CreateSwitch();

        // Assert
        Assert.Null(subject.IconColor);
    }

    [Fact]
    public void WhenConnectedAndRelayOff_ThenTurnOnIsEnabled()
    {
        // Arrange
        var subject = CreateSwitch();

        // Act
        subject.IsConnected = true;
        subject.IsOn = false;

        // Assert
        Assert.True(subject.TurnOn_IsEnabled);
    }

    [Fact]
    public void WhenDisconnected_ThenTurnOnIsDisabled()
    {
        // Arrange
        var subject = CreateSwitch();

        // Act
        subject.IsConnected = false;
        subject.IsOn = false;

        // Assert
        Assert.False(subject.TurnOn_IsEnabled);
    }

    [Fact]
    public void WhenConnectedAndRelayOnAndAllowTurnOff_ThenTurnOffIsEnabled()
    {
        // Arrange
        var subject = CreateSwitch();

        // Act
        subject.IsConnected = true;
        subject.IsOn = true;
        subject.AllowTurnOff = true;

        // Assert
        Assert.True(subject.TurnOff_IsEnabled);
    }

    [Fact]
    public void WhenAllowTurnOffIsFalse_ThenTurnOffIsDisabled()
    {
        // Arrange
        var subject = CreateSwitch();

        // Act
        subject.IsConnected = true;
        subject.IsOn = true;
        subject.AllowTurnOff = false;

        // Assert
        Assert.False(subject.TurnOff_IsEnabled);
    }

    [Fact]
    public void WhenIsWireless_ThenReturnsTrue()
    {
        // Arrange
        var subject = CreateSwitch();

        // Assert
        Assert.True(subject.IsWireless);
    }

    [Fact]
    public async Task WhenTheUptimeFalls_ThenTheInformationIsReadAgain()
    {
        // Arrange
        var device = new FakeDevice(uptimes: [100, 101, 5, 6], versions: ["4.0.7", "4.0.14"]);
        var subject = new MyStromSwitch(new FakeHttpClientFactory(device), NullLogger<MyStromSwitch>.Instance)
        {
            HostAddress = "switch.test",
            PollingInterval = TimeSpan.FromMilliseconds(1)
        };

        // Act
        await subject.StartAsync(CancellationToken.None);
        try
        {
            // Assert
            await AsyncTestHelpers.WaitUntilAsync(() => subject.FirmwareVersion == "4.0.14");
            Assert.Equal(2, device.InformationRequestCount);
        }
        finally
        {
            await subject.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenTheUptimeRises_ThenTheInformationIsNotReadAgain()
    {
        // Arrange
        var device = new FakeDevice(uptimes: [100, 101, 102, 103], versions: ["4.0.7", "4.0.14"]);
        var subject = new MyStromSwitch(new FakeHttpClientFactory(device), NullLogger<MyStromSwitch>.Instance)
        {
            HostAddress = "switch.test",
            PollingInterval = TimeSpan.FromMilliseconds(1)
        };

        // Act
        await subject.StartAsync(CancellationToken.None);
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(() => device.ReportRequestCount >= 6);

            // Assert
            Assert.Equal(1, device.InformationRequestCount);
            Assert.Equal("4.0.7", subject.FirmwareVersion);
        }
        finally
        {
            await subject.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void WhenHandlerIsCreated_ThenConnectTimeoutIsTenSeconds()
    {
        // Act
        using var handler = MyStromSwitch.CreateHttpHandler();

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(10), handler.ConnectTimeout);
    }

    [Fact]
    public async Task WhenOperating_ThenTheNamedClientWithoutRequestTimeoutIsUsed()
    {
        // Arrange
        var device = new FakeDevice(uptimes: [100], versions: ["4.0.7"]);
        var factory = new FakeHttpClientFactory(device);
        var subject = new MyStromSwitch(factory, NullLogger<MyStromSwitch>.Instance)
        {
            HostAddress = "switch.test",
            PollRequestTimeout = TimeSpan.Zero
        };

        // Act
        await subject.TurnOnAsync(CancellationToken.None);
        await subject.TurnOffAsync(CancellationToken.None);

        // Assert
        Assert.Equal(2, factory.Clients.Length);
        Assert.All(factory.Names, name => Assert.Equal("Namotion.Devices.MyStrom", name));
        Assert.All(factory.Clients, client => Assert.Equal(TimeSpan.FromSeconds(100), client.Timeout));
        Assert.Equal(true, subject.IsOn);
    }

    [Fact]
    public async Task WhenAPollIsNotAnswered_ThenTheSwitchIsInErrorAndReconnects()
    {
        // Arrange
        var device = new FakeDevice(uptimes: [100], versions: ["4.0.7"]) { HangReportRequestNumber = 2 };
        var subject = new MyStromSwitch(new FakeHttpClientFactory(device), NullLogger<MyStromSwitch>.Instance)
        {
            HostAddress = "switch.test",
            PollingInterval = TimeSpan.FromMilliseconds(1),
            RetryInterval = TimeSpan.FromMilliseconds(1),
            PollRequestTimeout = TimeSpan.FromMilliseconds(50)
        };

        // Act
        await subject.StartAsync(CancellationToken.None);
        try
        {
            // Assert
            await AsyncTestHelpers.WaitUntilAsync(() => device.InformationRequestCount >= 2 && subject.IsConnected);
            Assert.Equal(ServiceStatus.Running, subject.Status);
        }
        finally
        {
            await subject.StopAsync(CancellationToken.None);
        }
    }

    private class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class FakeHttpClientFactory(FakeDevice device) : IHttpClientFactory
    {
        private readonly ConcurrentQueue<HttpClient> _clients = new();

        private readonly ConcurrentQueue<string> _names = new();

        public HttpClient[] Clients => _clients.ToArray();

        public string[] Names => _names.ToArray();

        public HttpClient CreateClient(string name)
        {
            var client = new HttpClient(device, disposeHandler: false);
            _clients.Enqueue(client);
            _names.Enqueue(name);
            return client;
        }
    }

    /// <summary>
    /// Answers /info with the next of its versions and /report with the next of its uptimes, repeating the last one.
    /// </summary>
    private sealed class FakeDevice(long[] uptimes, string[] versions) : HttpMessageHandler
    {
        private int _informationRequestCount;
        private int _reportRequestCount;

        public int HangReportRequestNumber { get; init; }

        public int InformationRequestCount => Volatile.Read(ref _informationRequestCount);

        public int ReportRequestCount => Volatile.Read(ref _reportRequestCount);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/report" && HangReportRequestNumber > 0 &&
                Volatile.Read(ref _reportRequestCount) == HangReportRequestNumber - 1)
            {
                Interlocked.Increment(ref _reportRequestCount);
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            var json = request.RequestUri!.AbsolutePath switch
            {
                "/info" => $$"""{"version":"{{Next(versions, ref _informationRequestCount)}}"}""",
                "/report" => $$"""{"power":0,"relay":true,"energy_since_boot":0,"time_since_boot":{{Next(uptimes, ref _reportRequestCount)}}}""",
                _ => """{"measured":20,"compensation":0,"compensated":20}"""
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }

        private static T Next<T>(T[] values, ref int count)
        {
            var index = Interlocked.Increment(ref count) - 1;
            return values[Math.Min(index, values.Length - 1)];
        }
    }
}
