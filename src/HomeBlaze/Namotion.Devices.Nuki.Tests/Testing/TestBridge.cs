using System.Text.Json;
using HomeBlaze.Services.Lifecycle;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Devices.Nuki.Model;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Devices.Nuki.Tests.Testing;

/// <summary>
/// A bridge in a context with tracking, registry and operations, talking to a <see cref="FakeNukiBridge"/>.
/// </summary>
internal sealed class TestBridge : IAsyncDisposable
{
    public TestBridge(FakeNukiBridge? fake = null)
    {
        Fake = fake ?? new FakeNukiBridge();

        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithService<ILifecycleHandler>(
                () => new MethodPropertyInitializer(),
                handler => handler is MethodPropertyInitializer);

        Bridge = new NukiBridge(Fake, NullLogger<NukiBridge>.Instance)
        {
            HostAddress = "192.168.1.50",
            Token = "123456",
            TokenMode = NukiTokenMode.Plain,
            // Long, so that only the first poll after connecting runs unless a test shortens it.
            PollingInterval = TimeSpan.FromHours(1),
            RetryInterval = TimeSpan.FromSeconds(NukiBridge.MinimumIntervalSeconds),
            BusyRetryDelay = TimeSpan.Zero
        };
        ((IInterceptorSubject)Bridge).Context.AddFallbackContext(context);
    }

    public FakeNukiBridge Fake { get; }

    public NukiBridge Bridge { get; }

    public NukiSmartLock Lock => Bridge.Locks["1"];

    public NukiOpener Opener => Bridge.Openers["2"];

    /// <summary>Starts the bridge and waits until its first poll has run.</summary>
    public async Task StartAsync()
    {
        await Bridge.StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => Bridge.IsConnected && Bridge.LastUpdated is not null, message: "The bridge did not connect.");
    }

    /// <summary>Merges a device list without starting the bridge.</summary>
    public void Merge(string listJson) =>
        Bridge.MergeDevices(JsonSerializer.Deserialize(listJson, NukiJsonContext.Default.NukiDeviceResponseArray)!);

    public async ValueTask DisposeAsync()
    {
        await Bridge.StopAsync(CancellationToken.None);

        Bridge.Dispose();
    }
}
