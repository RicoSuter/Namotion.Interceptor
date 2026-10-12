using System.Text;
using HomeBlaze.Storage.Abstractions;
using HomeBlaze.Storage.Internal;
using Namotion.Interceptor;
using Namotion.Interceptor.Testing;

namespace HomeBlaze.Storage.Tests;

/// <summary>
/// Calls into the storage from code that runs on its worker. Queued, such a call would wait for the work that
/// waits for it, and the worker would never run again. It is refused instead, and the storage goes on.
/// </summary>
public class StorageReentrancyTests : StorageTestBase
{
    // A call that waits for itself never completes, so the wait for it ends the test instead.
    private static readonly TimeSpan DeadlockTimeout = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(nameof(IStorageContainer.WriteBlobAsync))]
    [InlineData(nameof(IStorageContainer.AddSubjectAsync))]
    [InlineData(nameof(IStorageContainer.DeleteBlobAsync))]
    [InlineData(nameof(IStorageContainer.DeleteSubjectAsync))]
    [InlineData(nameof(IConfigurationWriter.WriteConfigurationAsync))]
    public async Task WhenLoadOfFileCallsOperationOfItsStorage_ThenCallIsRefusedAndOnlyThatFileFails(string operation)
    {
        // Arrange
        WriteFile("Motor.json", SerializeMotor("From file"));
        WriteFile("Notes.md", "first");
        var storage = await ConnectAsync();
        var motor = Assert.IsType<Samples.Motor>(storage.Children["Motor"]);
        var notes = storage.Children["Notes.md"];
        WriteFile("Data.gated");
        WriteFile("Zulu.md");
        Exception? refusal = null;
        GatedFile.RunOnNextLoad(async (file, cancellationToken) =>
        {
            try
            {
                await CallAsync(operation, storage, motor, cancellationToken);
            }
            catch (Exception exception)
            {
                refusal = exception;
                throw;
            }
        });

        // Act
        await storage.ReconcileAsync().WaitAsync(DeadlockTimeout);
        var statusAfterPass = storage.Status;
        var childrenAfterPass = storage.Children.Keys.Order().ToList();
        await storage.ReconcileAsync(Named("Data.gated")).WaitAsync(DeadlockTimeout);

        // Assert: the pass went on with the files after the one whose load was refused, and so does the worker.
        Assert.Equal(StorageWorker.CallFromWithinItemMessage, Assert.IsType<InvalidOperationException>(refusal).Message);
        Assert.Equal(StorageStatus.Connected, statusAfterPass);
        Assert.Equal(["Motor", "Notes.md", "Zulu.md"], childrenAfterPass);
        Assert.Equal(["Data.gated", "Motor", "Notes.md", "Zulu.md"], storage.Children.Keys.Order());
        Assert.Same(motor, storage.Children["Motor"]);
        Assert.Same(notes, storage.Children["Notes.md"]);
        Assert.Equal("first", File.ReadAllText(GetFullPath("Notes.md")));
        Assert.Contains("From file", File.ReadAllText(GetFullPath("Motor.json")));
        Assert.False(File.Exists(GetFullPath("Added.json")));
    }

    [Fact]
    public async Task WhenRefreshAfterBlobWriteCallsItsStorage_ThenCallIsRefusedAndWriteStillSucceeds()
    {
        // Arrange
        WriteFile("Data.gated", "first");
        var storage = await ConnectAsync();
        var file = Assert.IsType<GatedFile>(storage.Children["Data.gated"]);
        Task? refusedCall = null;
        GatedFile.RunOnNextLoad((loaded, cancellationToken) =>
            refusedCall = loaded.Storage.AddSubjectAsync("Motor.json", CreateMotor("Added by the refresh"), cancellationToken));

        // Act
        await WriteBlobAsync(storage, "Data.gated", "second version", CancellationToken.None).WaitAsync(DeadlockTimeout);
        var contentAfterWrite = file.Content;
        await storage.ReconcileAsync().WaitAsync(DeadlockTimeout);

        // Assert
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => refusedCall!);
        Assert.Equal(StorageWorker.CallFromWithinItemMessage, refusal.Message);
        Assert.Equal("second version", File.ReadAllText(GetFullPath("Data.gated")));
        Assert.Equal("first", contentAfterWrite);
        Assert.Equal("second version", file.Content);
        Assert.Equal(StorageStatus.Connected, storage.Status);
        Assert.Equal(["Data.gated"], storage.Children.Keys);
    }

    [Fact]
    public async Task WhenConfigurationOfSubjectCallsItsStorageDuringPass_ThenCallIsRefusedAndPassCompletes()
    {
        // Arrange
        WriteFile("Valve.json", CallbackSubject.Serialize("Valve"));
        var storage = await ConnectAsync();
        var valve = Assert.IsType<CallbackSubject>(storage.Children["Valve"]);
        Task? refusedCall = null;
        CallbackSubject.RunOnNextApply((_, cancellationToken) =>
            refusedCall = storage.AddSubjectAsync("Devices/Motor.json", CreateMotor("Added by the configuration"), cancellationToken));
        WriteFile("Valve.json", CallbackSubject.Serialize("Valve, changed"));
        WriteFile("Zulu.md");

        // Act
        await storage.ReconcileAsync().WaitAsync(DeadlockTimeout);

        // Assert
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => refusedCall!);
        Assert.Equal(StorageWorker.CallFromWithinItemMessage, refusal.Message);
        Assert.Equal(StorageStatus.Connected, storage.Status);
        Assert.Equal(["Valve", "Zulu.md"], storage.Children.Keys.Order());
        Assert.Same(valve, storage.Children["Valve"]);
        Assert.False(Directory.Exists(GetFullPath("Devices")));
    }

    [Fact]
    public async Task WhenLoadStartsTaskItDoesNotAwait_ThenCallOfThatTaskIsRefusedWhileThePassRuns()
    {
        // Arrange
        var storage = await ConnectAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? detached = null;
        WriteFile("Data.gated");
        GatedFile.RunOnNextLoad((file, _) =>
        {
            // Background work that is started from the load with its execution flow.
            detached = Task.Run(async () =>
            {
                await started.Task;
                await file.Storage.AddSubjectAsync("Other.json", CreateMotor("Other"), CancellationToken.None);
            });
            return Task.CompletedTask;
        });
        var gate = GatedFile.PauseNextLoad();
        var pass = storage.ReconcileAsync();
        await gate.WhenReachedAsync();

        // Act: the pass is paused inside the load.
        started.SetResult();
        var exception = await Record.ExceptionAsync(() => detached!.WaitAsync(DeadlockTimeout));
        var childrenWhilePassIsPaused = storage.Children.Keys.ToList();
        gate.Release();
        await pass;

        // Assert
        Assert.Equal(StorageWorker.CallFromWithinItemMessage, Assert.IsType<InvalidOperationException>(exception).Message);
        Assert.Empty(childrenWhilePassIsPaused);
        Assert.Equal(["Data.gated"], storage.Children.Keys);
        Assert.False(File.Exists(GetFullPath("Other.json")));
    }

    [Fact]
    public async Task WhenLoadCallsItsStorageDirectlyWithinSuppressedFlow_ThenCallIsStillRefusedThroughItsTask()
    {
        // Arrange: suppressing the flow only keeps work that is started there from carrying it.
        WriteFile("Notes.md", "first");
        var storage = await ConnectAsync();
        Task? call = null;
        WriteFile("Data.gated");
        GatedFile.RunOnNextLoad((file, _) =>
        {
            using (ExecutionContext.SuppressFlow())
            {
                call = file.Storage.WriteBlobAsync("Notes.md", new MemoryStream("second"u8.ToArray()), CancellationToken.None);
            }

            return Task.CompletedTask;
        });

        // Act
        await storage.ReconcileAsync();
        var exception = await Record.ExceptionAsync(() => call!.WaitAsync(DeadlockTimeout));

        // Assert: the load that did not await the call has not failed, and nothing was written.
        Assert.Equal(StorageWorker.CallFromWithinItemMessage, Assert.IsType<InvalidOperationException>(exception).Message);
        Assert.Equal(["Data.gated", "Notes.md"], storage.Children.Keys.Order());
        Assert.Equal("first", File.ReadAllText(GetFullPath("Notes.md")));
    }

    [Fact]
    public async Task WhenLoadStartsTaskWithoutExecutionFlow_ThenCallOfThatTaskRunsAfterThePass()
    {
        // Arrange
        var storage = await ConnectAsync();
        var other = CreateMotor("Other");
        Task? detached = null;
        WriteFile("Data.gated");
        GatedFile.RunOnNextLoad((file, _) =>
        {
            using (ExecutionContext.SuppressFlow())
            {
                detached = Task.Run(() => file.Storage.AddSubjectAsync("Other.json", other, CancellationToken.None));
            }

            return Task.CompletedTask;
        });
        var gate = GatedFile.PauseNextLoad();
        var pass = storage.ReconcileAsync();
        await gate.WhenReachedAsync();

        // Act
        var addWasWaitingForPass = !detached!.IsCompleted;
        var childrenWhilePassIsPaused = storage.Children.Keys.ToList();
        gate.Release();
        await pass;
        await detached.WaitAsync(DeadlockTimeout);

        // Assert
        Assert.True(addWasWaitingForPass);
        Assert.Empty(childrenWhilePassIsPaused);
        Assert.Equal(["Data.gated", "Other"], storage.Children.Keys.Order());
        Assert.Same(other, storage.Children["Other"]);
    }

    [Fact]
    public async Task WhenPassOfParentReconnectsNestedStorageWhileItWorks_ThenBothCompleteAndNestedTreeMatchesItsNewStorage()
    {
        // Arrange
        var nestedDirectory = CreateTemporaryDirectory();
        var otherDirectory = CreateTemporaryDirectory();
        File.WriteAllText(Path.Combine(nestedDirectory.FullName, "Old.md"), "content");
        File.WriteAllText(Path.Combine(otherDirectory.FullName, "New.md"), "content");
        WriteFile("Nested.json", SerializeStorage(nestedDirectory.FullName));

        var parent = await ConnectAsync();
        var nested = Assert.IsType<FluentStorageContainer>(parent.Children["Nested"]);
        try
        {
            // Connected here, because no host runs in this test that would start the nested storage.
            await nested.ConnectAsync(CancellationToken.None);
            var childrenBeforeReconnect = nested.Children.Keys.ToList();

            File.WriteAllText(Path.Combine(nestedDirectory.FullName, "Slow.gated"), "content");
            var gate = GatedFile.PauseNextLoad();
            var nestedPass = nested.ReconcileAsync();
            await gate.WhenReachedAsync();
            WriteFile("Nested.json", SerializeStorage(otherDirectory.FullName));

            // Act: the pass of the parent applies the new configuration, which waits for the pass of the nested storage.
            var parentPass = parent.ReconcileAsync();
            await AsyncTestHelpers.WaitUntilAsync(() => nested.Status == StorageStatus.Initializing, WatcherTimeout);
            var parentPassWasWaiting = !parentPass.IsCompleted;
            gate.Release();
            await parentPass.WaitAsync(DeadlockTimeout);
            await nestedPass.WaitAsync(DeadlockTimeout);

            // Assert
            Assert.Equal(["Old.md"], childrenBeforeReconnect);
            Assert.True(parentPassWasWaiting);
            Assert.Equal(StorageStatus.Connected, parent.Status);
            Assert.Same(nested, parent.Children["Nested"]);
            Assert.Equal(StorageStatus.Connected, nested.Status);
            Assert.Equal(["New.md"], nested.Children.Keys);
        }
        finally
        {
            nested.Dispose();
        }
    }

    private Task CallAsync(
        string operation, FluentStorageContainer storage, IInterceptorSubject subjectInStorage, CancellationToken cancellationToken)
        => operation switch
        {
            nameof(IStorageContainer.WriteBlobAsync) => WriteBlobAsync(storage, "Notes.md", "written by the load", cancellationToken),
            nameof(IStorageContainer.AddSubjectAsync) => storage.AddSubjectAsync("Added.json", CreateMotor("Added by the load"), cancellationToken),
            nameof(IStorageContainer.DeleteBlobAsync) => storage.DeleteBlobAsync("Notes.md", cancellationToken),
            nameof(IStorageContainer.DeleteSubjectAsync) => storage.DeleteSubjectAsync(subjectInStorage, cancellationToken),
            nameof(IConfigurationWriter.WriteConfigurationAsync) => storage.WriteConfigurationAsync(subjectInStorage, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

    private static async Task WriteBlobAsync(
        IStorageContainer storage, string path, string content, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        await storage.WriteBlobAsync(path, stream, cancellationToken);
    }
}
