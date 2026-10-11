using System.Collections.Concurrent;
using FluentStorage.Blobs;
using HomeBlaze.Storage.Abstractions;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Internal;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Storage.Tests;

/// <summary>
/// What becomes of a subject that is a hosted service when the storage creates it and does not place it. A host
/// runs here, so the storage is started by the hosted service handler as in the application, and a device is
/// started when it enters the context and stopped when it leaves it.
/// </summary>
public class StorageHostedSubjectTests : StorageTestBase, IAsyncLifetime
{
    private readonly ManualTimeProvider _timeProvider = new();
    private readonly LifecycleRecorder _recorder = new();
    private PausableBlobStorage? _client;
    private IHostedService? _host;

    public StorageHostedSubjectTests()
    {
        RecordingDevice.Reset();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_host != null)
        {
            await _host.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenFirstPassTimesOutAfterCreatingDeviceAndStorageConnectsAgain_ThenThatDeviceIsDetachedAndStopped()
    {
        // Arrange: the first pass creates the device of A.json, loads B.gated and stalls on C.gated.
        WriteFile("A.json", RecordingDevice.Serialize("A"));
        WriteFile("B.gated");
        WriteFile("C.gated");
        var storage = await StartStorageWhoseFirstPassTimesOutAsync();
        var first = Assert.Single(RecordingDevice.Created);
        await first.WhenStartedAsync();
        var childrenAfterFailedPass = storage.Children.Keys.ToList();

        // Act
        await storage.ConnectAsync(CancellationToken.None);

        // Assert
        Assert.Empty(childrenAfterFailedPass);
        Assert.Equal(2, RecordingDevice.Created.Count);
        var second = RecordingDevice.Created[1];
        Assert.Same(second, storage.Children["A"]);
        await second.WhenStartedAsync();
        Assert.False(second.IsStopped);

        await first.WhenStoppedAsync();
        Assert.Equal(1, _recorder.CountDetachesOf(first));
        Assert.False(IsAttached(first));
    }

    [Fact]
    public async Task WhenConnectIsCancelledAfterCreatingDeviceAndStorageConnectsAgain_ThenThatDeviceIsDetachedAndStopped()
    {
        // Arrange
        var storage = await StartHostedStorageAsync();
        await storage.ExecuteTask!;
        WriteFile("A.json", RecordingDevice.Serialize("A"));
        WriteFile("B.gated");
        WriteFile("C.gated");
        var gate = GatedFile.PauseNextLoad();
        using var cancellation = new CancellationTokenSource();
        var connect = storage.ConnectAsync(cancellation.Token);
        await gate.WhenReachedAsync();
        var first = Assert.Single(RecordingDevice.Created);
        await first.WhenStartedAsync();
        await cancellation.CancelAsync();
        gate.Release();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect);

        // Act
        await storage.ConnectAsync(CancellationToken.None);

        // Assert
        Assert.Equal(2, RecordingDevice.Created.Count);
        Assert.Same(RecordingDevice.Created[1], storage.Children["A"]);
        await first.WhenStoppedAsync();
        Assert.Equal(1, _recorder.CountDetachesOf(first));
        Assert.False(IsAttached(first));
    }

    [Fact]
    public async Task WhenFailedPassIsFollowedByPassOfSameConnection_ThenItsDeviceIsPlacedAndNeverDetached()
    {
        // Arrange
        WriteFile("A.json", RecordingDevice.Serialize("A"));
        WriteFile("B.gated");
        WriteFile("C.gated");
        var storage = await StartStorageWhoseFirstPassTimesOutAsync();

        // Act
        _client!.Release();
        await AsyncTestHelpers.WaitUntilAsync(() => !storage.IsStorageUnresponsive);
        await storage.ReconcileAsync();

        // Assert
        var device = Assert.Single(RecordingDevice.Created);
        Assert.Same(device, storage.Children["A"]);
        Assert.Equal(StorageStatus.Connected, storage.Status);
        Assert.Equal(0, _recorder.CountDetachesOf(device));
        Assert.False(device.IsStopped);
    }

    [Fact]
    public async Task WhenKeyOfJsonSubjectIsTaken_ThenEveryDeviceCreatedForItIsDetachedAndStopped()
    {
        // Arrange: the folder "Device" holds the key that Device.json asks for.
        WriteFile("Device.json", RecordingDevice.Serialize("first"));
        WriteFile("Device/Page.md");
        var storage = await StartHostedStorageAsync();
        await storage.ExecuteTask!;

        // Act
        WriteFile("Device.json", RecordingDevice.Serialize("second, with another size"));
        await storage.ReconcileAsync();
        WriteFile("Device.json", RecordingDevice.Serialize("third, with yet another size"));
        await storage.ReconcileAsync();

        // Assert
        var devices = RecordingDevice.Created;
        await WaitUntilHostHasHandledEveryChangeAsync();
        Assert.IsType<VirtualFolder>(storage.Children["Device"]);
        Assert.Equal(3, devices.Count);
        foreach (var device in devices)
        {
            Assert.Equal(1, _recorder.CountAttachesOf(device));
            Assert.Equal(1, _recorder.CountDetachesOf(device));
            Assert.False(IsAttached(device));
            Assert.True(device.IsStopped);
        }
    }

    [Fact]
    public async Task WhenFileOfUnplacedDeviceChangesItsType_ThenThatDeviceIsDetachedAndStopped()
    {
        // Arrange: the first pass creates the device and ends before it places anything.
        WriteFile("A.json", RecordingDevice.Serialize("A"));
        WriteFile("B.gated");
        WriteFile("C.gated");
        var storage = await StartStorageWhoseFirstPassTimesOutAsync();
        var device = Assert.Single(RecordingDevice.Created);
        await device.WhenStartedAsync();
        _client!.Release();
        await AsyncTestHelpers.WaitUntilAsync(() => !storage.IsStorageUnresponsive);

        // Act
        WriteFile("A.json", SerializeMotor("Now a motor"));
        await storage.ReconcileAsync();

        // Assert
        Assert.IsType<Samples.Motor>(storage.Children["A"]);
        await device.WhenStoppedAsync();
        Assert.Equal(1, _recorder.CountDetachesOf(device));
        Assert.False(IsAttached(device));
    }

    [Fact]
    public async Task WhenFileOfUnplacedDeviceIsDeleted_ThenThatDeviceIsDetachedAndStopped()
    {
        // Arrange: the first pass creates the device and ends before it places anything.
        WriteFile("A.json", RecordingDevice.Serialize("A"));
        WriteFile("B.gated");
        WriteFile("C.gated");
        var storage = await StartStorageWhoseFirstPassTimesOutAsync();
        var device = Assert.Single(RecordingDevice.Created);
        await device.WhenStartedAsync();
        _client!.Release();
        await AsyncTestHelpers.WaitUntilAsync(() => !storage.IsStorageUnresponsive);

        // Act
        File.Delete(GetFullPath("A.json"));
        await storage.ReconcileAsync();

        // Assert
        Assert.Equal(["B.gated", "C.gated"], storage.Children.Keys.Order());
        await device.WhenStoppedAsync();
        Assert.Equal(1, _recorder.CountDetachesOf(device));
        Assert.False(IsAttached(device));
    }

    [Fact]
    public async Task WhenPlacedDeviceIsRemoved_ThenItIsDetachedOnce()
    {
        // Arrange
        WriteFile("A.json", RecordingDevice.Serialize("A"));
        var storage = await StartHostedStorageAsync();
        await storage.ExecuteTask!;
        var device = Assert.Single(RecordingDevice.Created);
        await device.WhenStartedAsync();

        // Act
        await storage.DeleteSubjectAsync(device, CancellationToken.None);

        // Assert
        Assert.Empty(storage.Children);
        await device.WhenStoppedAsync();
        Assert.Equal(1, _recorder.CountAttachesOf(device));
        Assert.Equal(1, _recorder.CountDetachesOf(device));
    }

    [Fact]
    public async Task WhenNestedStorageIsRemovedWhileItsPassRuns_ThenDeviceThatPassCreatesIsDetachedAndStopped()
    {
        // Arrange
        var nestedDirectory = CreateTemporaryDirectory();
        File.WriteAllText(Path.Combine(nestedDirectory.FullName, "Old.json"), RecordingDevice.Serialize("old"));
        WriteFile("Nested.json", SerializeStorage(nestedDirectory.FullName));
        var parent = await StartHostedStorageAsync();
        await parent.ExecuteTask!;
        var nested = Assert.IsType<FluentStorageContainer>(parent.Children["Nested"]);
        try
        {
            // Started by the host, as the storage that holds it.
            await AsyncTestHelpers.WaitUntilAsync(() => nested.ExecuteTask != null);
            await nested.ExecuteTask!;
            var old = Assert.Single(RecordingDevice.Created);

            File.WriteAllText(Path.Combine(nestedDirectory.FullName, "Slow.gated"), "content");
            File.WriteAllText(Path.Combine(nestedDirectory.FullName, "Z.json"), RecordingDevice.Serialize("late"));
            var gate = GatedFile.PauseNextLoad();
            var nestedPass = nested.ReconcileAsync();
            await gate.WhenReachedAsync();

            // Act: the parent removes the nested storage while the pass of the nested storage runs.
            File.Delete(GetFullPath("Nested.json"));
            await parent.ReconcileAsync();
            gate.Release();
            await nestedPass;

            // Assert
            var devices = RecordingDevice.Created;
            await WaitUntilHostHasHandledEveryChangeAsync();
            Assert.Empty(parent.Children);
            Assert.True(old.IsStopped);
            Assert.Equal(2, devices.Count);
            var late = devices[1];
            Assert.Equal(1, _recorder.CountAttachesOf(late));
            Assert.Equal(1, _recorder.CountDetachesOf(late));
            Assert.False(IsAttached(late));
            Assert.True(late.IsStopped);
        }
        finally
        {
            nested.Dispose();
        }
    }

    [Fact]
    public async Task WhenKeyOfPageIsTaken_ThenEveryDeviceEmbeddedInItIsDetachedAndStopped()
    {
        // Arrange: the subject of Page.md.json holds the key that Page.md asks for.
        WriteFile("Page.md.json", SerializeMotor("Holds the key"));
        var storage = await StartHostedStorageAsync();
        await storage.ExecuteTask!;

        // Act
        WriteFile("Page.md", SerializePageWithDevice("first"));
        await storage.ReconcileAsync();
        WriteFile("Page.md", SerializePageWithDevice("second, with another size"));
        await storage.ReconcileAsync();

        // Assert
        var devices = RecordingDevice.Created;
        await WaitUntilHostHasHandledEveryChangeAsync();
        Assert.IsType<Samples.Motor>(storage.Children["Page.md"]);
        Assert.Equal(2, devices.Count);
        foreach (var device in devices)
        {
            Assert.Equal(1, _recorder.CountAttachesOf(device));
            Assert.Equal(1, _recorder.CountDetachesOf(device));
            Assert.False(IsAttached(device));
            Assert.True(device.IsStopped);
        }
    }

    [Fact]
    public async Task WhenFirstPassTimesOutAfterLoadingPageAndStorageConnectsAgain_ThenDeviceEmbeddedInThatPageIsDetachedAndStopped()
    {
        // Arrange: the first pass loads the page with its device, loads B.gated and stalls on C.gated.
        WriteFile("A.md", SerializePageWithDevice("A"));
        WriteFile("B.gated");
        WriteFile("C.gated");
        var storage = await StartStorageWhoseFirstPassTimesOutAsync();
        var first = Assert.Single(RecordingDevice.Created);
        await first.WhenStartedAsync();

        // Act
        await storage.ConnectAsync(CancellationToken.None);

        // Assert
        Assert.Equal(2, RecordingDevice.Created.Count);
        var second = RecordingDevice.Created[1];
        Assert.Same(second, Assert.IsType<MarkdownFile>(storage.Children["A.md"]).Children["device"]);
        await second.WhenStartedAsync();
        Assert.False(second.IsStopped);

        await first.WhenStoppedAsync();
        Assert.Equal(1, _recorder.CountAttachesOf(first));
        Assert.Equal(1, _recorder.CountDetachesOf(first));
        Assert.False(IsAttached(first));
    }

    [Fact]
    public async Task WhenFileOfUnplacedPageIsDeleted_ThenDeviceEmbeddedInItIsDetachedAndStopped()
    {
        // Arrange: the first pass loads the page with its device and ends before it places anything.
        WriteFile("A.md", SerializePageWithDevice("A"));
        WriteFile("B.gated");
        WriteFile("C.gated");
        var storage = await StartStorageWhoseFirstPassTimesOutAsync();
        var device = Assert.Single(RecordingDevice.Created);
        await device.WhenStartedAsync();
        _client!.Release();
        await AsyncTestHelpers.WaitUntilAsync(() => !storage.IsStorageUnresponsive);

        // Act
        File.Delete(GetFullPath("A.md"));
        await storage.ReconcileAsync();

        // Assert
        Assert.Equal(["B.gated", "C.gated"], storage.Children.Keys.Order());
        await device.WhenStoppedAsync();
        Assert.Equal(1, _recorder.CountAttachesOf(device));
        Assert.Equal(1, _recorder.CountDetachesOf(device));
        Assert.False(IsAttached(device));
    }

    [Fact]
    public async Task WhenFailedPassIsFollowedByPassOfSameConnection_ThenDeviceEmbeddedInItsPageIsNeverDetached()
    {
        // Arrange
        WriteFile("A.md", SerializePageWithDevice("A"));
        WriteFile("B.gated");
        WriteFile("C.gated");
        var storage = await StartStorageWhoseFirstPassTimesOutAsync();

        // Act
        _client!.Release();
        await AsyncTestHelpers.WaitUntilAsync(() => !storage.IsStorageUnresponsive);
        await storage.ReconcileAsync();

        // Assert
        var device = Assert.Single(RecordingDevice.Created);
        Assert.Same(device, Assert.IsType<MarkdownFile>(storage.Children["A.md"]).Children["device"]);
        Assert.Equal(1, _recorder.CountAttachesOf(device));
        Assert.Equal(0, _recorder.CountDetachesOf(device));
        Assert.False(device.IsStopped);
    }

    [Fact]
    public async Task WhenPlacedPageIsRemoved_ThenDeviceEmbeddedInItIsDetachedOnce()
    {
        // Arrange
        WriteFile("A.md", SerializePageWithDevice("A"));
        var storage = await StartHostedStorageAsync();
        await storage.ExecuteTask!;
        var device = Assert.Single(RecordingDevice.Created);
        await device.WhenStartedAsync();

        // Act
        File.Delete(GetFullPath("A.md"));
        await storage.ReconcileAsync();

        // Assert
        Assert.Empty(storage.Children);
        await device.WhenStoppedAsync();
        Assert.Equal(1, _recorder.CountAttachesOf(device));
        Assert.Equal(1, _recorder.CountDetachesOf(device));
    }

    private static string SerializePageWithDevice(string deviceName)
        => $"# Page\n\n```subject(device)\n{RecordingDevice.Serialize(deviceName)}\n```\n";

    private async Task<FluentStorageContainer> StartHostedStorageAsync()
    {
        var storage = CreateStorage(configure: container =>
        {
            container.TimeProvider = _timeProvider;
            container.ClientDecorator = client => _client = new PausableBlobStorage(client);
        });

        Context!.AddService<ILifecycleHandler>(_recorder);

        // The hosted service handler of the context. Started, it starts the storage, which connects.
        _host = Context.GetServices<ILifecycleHandler>().OfType<IHostedService>().Single();
        await _host.StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => storage.ExecuteTask != null);
        return storage;
    }

    /// <summary>
    /// Starts a storage whose first pass loads the first two files and is given up at the third, which needs
    /// one file with the extension of <see cref="GatedFile"/> before the file it stalls on.
    /// </summary>
    private async Task<FluentStorageContainer> StartStorageWhoseFirstPassTimesOutAsync()
    {
        var gate = GatedFile.PauseNextLoad();
        var storage = await StartHostedStorageAsync();
        await gate.WhenReachedAsync();
        var idleTimerCount = _timeProvider.ArmedTimerCount;
        var readReached = _client!.PauseNext(nameof(IBlobStorage.OpenReadAsync));
        gate.Release();
        await readReached;
        await AsyncTestHelpers.WaitUntilAsync(() => _timeProvider.ArmedTimerCount == idleTimerCount + 1);
        _timeProvider.Advance(StorageCallTimeout.Limit);
        await Assert.ThrowsAsync<StorageUnresponsiveException>(() => storage.ExecuteTask!);
        Assert.Equal(StorageStatus.Error, storage.Status);
        return storage;
    }

    /// <summary>
    /// The host starts and stops its services one after another, in the order in which they entered and left
    /// the context. Once it has started a device created now, it has handled everything before it.
    /// </summary>
    private async Task WaitUntilHostHasHandledEveryChangeAsync()
    {
        var last = new RecordingDevice(Context!);
        await last.WhenStartedAsync();
    }

    private bool IsAttached(IInterceptorSubject subject)
        => Context!.TryGetLifecycleInterceptor()!.TryRunWhileAttached(subject, static () => { });

    private sealed class LifecycleRecorder : ILifecycleHandler
    {
        private readonly ConcurrentQueue<IInterceptorSubject> _attached = new();
        private readonly ConcurrentQueue<IInterceptorSubject> _detached = new();

        public int CountAttachesOf(IInterceptorSubject subject)
            => _attached.Count(attached => ReferenceEquals(attached, subject));

        public int CountDetachesOf(IInterceptorSubject subject)
            => _detached.Count(detached => ReferenceEquals(detached, subject));

        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            if (change.IsContextAttach)
            {
                _attached.Enqueue(change.Subject);
            }

            if (change.IsContextDetach)
            {
                _detached.Enqueue(change.Subject);
            }
        }
    }
}
