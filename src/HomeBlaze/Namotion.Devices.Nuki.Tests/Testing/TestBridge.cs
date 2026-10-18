using System.Text.Json;
using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Devices.Nuki.Model;
using Namotion.Interceptor;
using Namotion.Interceptor.Testing;

namespace Namotion.Devices.Nuki.Tests.Testing;

/// <summary>
/// A bridge in a context wired like the host wires its subjects, talking to a <see cref="FakeNukiBridge"/>.
/// </summary>
internal sealed class TestBridge : IAsyncDisposable
{
    public TestBridge(FakeNukiBridge? fake = null)
    {
        Fake = fake ?? new FakeNukiBridge();
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
        ((IInterceptorSubject)Bridge).Context.AddFallbackContext(SubjectContextFactory.Create(new ServiceCollection()));
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

    /// <summary>
    /// Starts an action that the fake bridge holds, like a bridge waits for the motor, and waits until it was sent.
    /// </summary>
    public async Task<HeldAction> StartHeldActionAsync(Func<Task> startAction)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Fake.ActionGate = gate;
        var sentActions = Fake.CountRequests("lockAction");
        var action = startAction();
        await AsyncTestHelpers.WaitUntilAsync(() => Fake.CountRequests("lockAction") > sentActions, message: "The action was not sent.");
        return new HeldAction(action, gate);
    }

    public async ValueTask DisposeAsync()
    {
        await Bridge.StopAsync(CancellationToken.None);

        Bridge.Dispose();
    }

    /// <summary>An action that the fake bridge holds until it is released.</summary>
    public sealed class HeldAction(Task action, TaskCompletionSource gate)
    {
        /// <summary>The running action.</summary>
        public Task Action => action;

        /// <summary>Lets the bridge finish the action and returns the action.</summary>
        public Task ReleaseAsync()
        {
            gate.TrySetResult();
            return action;
        }
    }
}
