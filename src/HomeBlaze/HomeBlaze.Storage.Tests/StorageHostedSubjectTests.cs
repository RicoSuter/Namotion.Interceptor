using System.Collections.Concurrent;
using FluentStorage.Blobs;
using HomeBlaze.Storage.Abstractions;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Internal;
using HomeBlaze.Storage.Pages;
using HomeBlaze.Storage.Tests.Pages;
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
    public async Task WhenStorageIsStoppedAfterPassThatEndedEarly_ThenDeviceThatPassCreatedIsDetachedAndStopped()
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

        // Act: what the host does with a storage that leaves the tree, such as one whose file was deleted.
        await storage.StopAsync(CancellationToken.None);

        // Assert
        await device.WhenStoppedAsync();
        Assert.Empty(storage.Children);
        Assert.Equal(1, _recorder.CountDetachesOf(device));
        Assert.False(IsAttached(device));
    }

    [Fact]
    public async Task WhenSubjectIsAddedAfterStopReleasedWhatPassCreated_ThenThatIsOnlyLoadedWhenStorageIsStartedAgain()
    {
        // Arrange: the first pass creates the device, loads B.gated and ends before it places anything, and
        // the stop releases the device.
        WriteFile("A.json", RecordingDevice.Serialize("A"));
        WriteFile("B.gated");
        WriteFile("C.gated");
        var storage = await StartStorageWhoseFirstPassTimesOutAsync();
        var device = Assert.Single(RecordingDevice.Created);
        await device.WhenStartedAsync();
        _client!.Release();
        await AsyncTestHelpers.WaitUntilAsync(() => !storage.IsStorageUnresponsive);
        await storage.StopAsync(CancellationToken.None);
        await device.WhenStoppedAsync();

        // Act: an operation assigns the tree from the index.
        await storage.AddSubjectAsync("New.json", CreateMotor("New"), CancellationToken.None);
        var childrenAfterAdd = storage.Children.Keys.ToList();
        await storage.StartAsync(CancellationToken.None);
        await storage.ExecuteTask!;

        // Assert
        Assert.Equal(["New"], childrenAfterAdd);
        Assert.Equal(1, _recorder.CountAttachesOf(device));
        Assert.False(IsAttached(device));
        Assert.Equal(["A", "B.gated", "C.gated", "New"], storage.Children.Keys.Order());
        Assert.Equal(2, RecordingDevice.Created.Count);
        Assert.Same(RecordingDevice.Created[1], storage.Children["A"]);
    }

    [Fact]
    public async Task WhenStorageIsStoppedDuringItsFirstPass_ThenDeviceThatPassCreatedIsDetachedAndStopped()
    {
        // Arrange
        WriteFile("A.json", RecordingDevice.Serialize("A"));
        WriteFile("B.gated");
        WriteFile("C.gated");
        var gate = GatedFile.PauseNextLoad();
        var storage = await StartHostedStorageAsync();
        await gate.WhenReachedAsync();
        var device = Assert.Single(RecordingDevice.Created);
        await device.WhenStartedAsync();

        // Act: the stop cancels the first pass, which ends before it places anything.
        var stop = storage.StopAsync(CancellationToken.None);
        gate.Release();
        await stop;

        // Assert
        await device.WhenStoppedAsync();
        Assert.Empty(storage.Children);
        Assert.Equal(1, _recorder.CountDetachesOf(device));
        Assert.False(IsAttached(device));
    }

    [Fact]
    public async Task WhenStorageIsStopped_ThenPlacedDeviceIsNeitherDetachedNorStopped()
    {
        // Arrange
        WriteFile("A.json", RecordingDevice.Serialize("A"));
        WriteFile("Page.md", SerializePageWithDevice("embedded"));
        var storage = await StartHostedStorageAsync();
        await storage.ExecuteTask!;
        var devices = RecordingDevice.Created;

        // Act
        await storage.StopAsync(CancellationToken.None);
        await storage.WriteConfigurationAsync(devices[0], CancellationToken.None);

        // Assert: the save ran on the worker behind what the stop left there.
        await WaitUntilHostHasHandledEveryChangeAsync();
        Assert.Equal(2, devices.Count);
        foreach (var device in devices)
        {
            Assert.Equal(0, _recorder.CountDetachesOf(device));
            Assert.True(IsAttached(device));
            Assert.False(device.IsStopped);
        }
    }

    [Theory]
    [InlineData("X/A.json", "X/A/Page.md", "A.json")]
    [InlineData("Docs.json", "Other/Page.md", "Other/Docs.json")]
    public async Task WhenDeviceMovesToAnotherFolderAndFolderTakesOverItsKey_ThenItIsNeitherDetachedNorStopped(
        string devicePath, string pagePath, string movedPath)
    {
        // Arrange: in the first row the folder of the page asks for the key that the device holds, and is left out.
        WriteFile(devicePath, RecordingDevice.Serialize("device"));
        var storage = await StartHostedStorageAsync();
        await storage.ExecuteTask!;
        var device = Assert.Single(RecordingDevice.Created);
        await device.WhenStartedAsync();
        WriteFile(pagePath);
        await storage.ReconcileAsync();

        // Act
        Directory.CreateDirectory(Path.GetDirectoryName(GetFullPath(movedPath))!);
        File.Move(GetFullPath(devicePath), GetFullPath(movedPath));
        await storage.ReconcileAsync();

        // Assert
        await WaitUntilHostHasHandledEveryChangeAsync();
        Assert.Same(device, FindSubject(storage, movedPath[..^".json".Length]));
        Assert.IsType<MarkdownFile>(FindSubject(storage, pagePath));
        Assert.Single(RecordingDevice.Created, created => created.Name == "device");
        Assert.Equal(0, _recorder.CountDetachesOf(device));
        Assert.False(device.IsStopped);
    }

    [Fact]
    public async Task WhenDeviceMovesToKeyOfFileThatIsDeletedInTheSamePass_ThenItIsNeitherDetachedNorStopped()
    {
        // Arrange: the plain file "A" holds the key that the device asks for once its file is in the same folder.
        WriteFile("Source/A.json", RecordingDevice.Serialize("device"));
        WriteFile("Target/A");
        var storage = await StartHostedStorageAsync();
        await storage.ExecuteTask!;
        var device = Assert.Single(RecordingDevice.Created);
        await device.WhenStartedAsync();

        // Act
        File.Delete(GetFullPath("Target/A"));
        File.Move(GetFullPath("Source/A.json"), GetFullPath("Target/A.json"));
        await storage.ReconcileAsync();

        // Assert
        await WaitUntilHostHasHandledEveryChangeAsync();
        Assert.Same(device, FindSubject(storage, "Target/A"));
        Assert.Single(RecordingDevice.Created, created => created.Name == "device");
        Assert.Equal(0, _recorder.CountDetachesOf(device));
        Assert.False(device.IsStopped);
    }

    [Fact]
    public async Task WhenFileOfUnplacedDeviceIsDeletedThroughTheStorage_ThenThatDeviceIsDetachedAndStopped()
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
        await storage.DeleteBlobAsync("A.json", CancellationToken.None);

        // Assert
        Assert.False(File.Exists(GetFullPath("A.json")));
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
    public async Task WhenKeyOfPageIsTaken_ThenEveryDeviceNestedInSlotsOfItsWidgetsIsDetachedAndStopped()
    {
        // Arrange: the subject of Page.md.json holds the key that Page.md asks for.
        WriteFile("Page.md.json", SerializeMotor("Holds the key"));
        var storage = await StartHostedStorageAsync();
        await storage.ExecuteTask!;

        // Act
        WriteFile("Page.md", SerializePageWithNestedDevices());
        await storage.ReconcileAsync();

        // Assert
        var devices = RecordingDevice.Created;
        await WaitUntilHostHasHandledEveryChangeAsync();
        Assert.IsType<Samples.Motor>(storage.Children["Page.md"]);
        Assert.Equal(["content", "row", "rowOfRow"], devices.Select(device => device.Name).Order());
        foreach (var device in devices)
        {
            Assert.Equal(1, _recorder.CountAttachesOf(device));
            Assert.Equal(1, _recorder.CountDetachesOf(device));
            Assert.False(IsAttached(device));
            Assert.True(device.IsStopped);
        }
    }

    [Fact]
    public async Task WhenPlacedPageIsRemoved_ThenEveryDeviceNestedInSlotsOfItsWidgetsIsDetachedOnce()
    {
        // Arrange
        WriteFile("A.md", SerializePageWithNestedDevices());
        var storage = await StartHostedStorageAsync();
        await storage.ExecuteTask!;
        var devices = RecordingDevice.Created;
        var panel = Assert.IsType<TestPanel>(Assert.IsType<MarkdownFile>(storage.Children["A.md"]).Children["panel"]);
        var attachedBeforeRemoval = devices.Select(IsAttached).ToList();

        // Act
        File.Delete(GetFullPath("A.md"));
        await storage.ReconcileAsync();

        // Assert
        await WaitUntilHostHasHandledEveryChangeAsync();
        Assert.Empty(storage.Children);
        Assert.Equal(["content", "row", "rowOfRow"], devices.Select(device => device.Name).Order());
        Assert.Equal([true, true, true], attachedBeforeRemoval);
        Assert.Contains(devices, device => ReferenceEquals(device, Assert.IsType<MarkdownFragment>(panel.Content).Children["inContent"]));
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

    /// <summary>The subject under the keys of a path, which name a folder each up to the last one.</summary>
    private static IInterceptorSubject FindSubject(FluentStorageContainer storage, string keyPath)
    {
        var keys = keyPath.Split('/');
        var children = storage.Children;
        foreach (var key in keys[..^1])
        {
            children = Assert.IsType<VirtualFolder>(children[key]).Children;
        }

        return children[keys[^1]];
    }

    private static string SerializePageWithDevice(string deviceName)
        => $"# Page\n\n{RecordingDevice.SerializeBlock("device", deviceName)}";

    /// <summary>
    /// A page with a panel that holds one device in the fragment of its default slot, one in the fragment of a
    /// row of its typed slot, and one in a row that is itself in that fragment.
    /// </summary>
    private static string SerializePageWithNestedDevices()
    {
        var panel = typeof(TestPanel).FullName;
        var row = typeof(TestRow).FullName;
        var device = typeof(RecordingDevice).FullName;
        return $"# Page\n\n:: {panel}#panel\n" +
               $"::: Row Label=A\n" +
               $":::: {device}#inRow Name=row\n::::\n" +
               $":::: {row}#rowInRow\n::::: {device}#inRowOfRow Name=rowOfRow\n:::::\n::::\n" +
               $":::\n\n" +
               $"::: {device}#inContent Name=content\n:::\n" +
               $"::\n";
    }

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
