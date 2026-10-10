using System.Text;
using HomeBlaze.Storage.Abstractions;
using HomeBlaze.Storage.Files;
using Namotion.Interceptor;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Storage.Tests;

public class StorageReconcilerTests : StorageTestBase
{
    [Fact]
    public async Task WhenNothingChanged_ThenPassLoadsNoFile()
    {
        // Arrange
        WriteFile("Data.gated");
        var storage = await ConnectAsync();
        var loadCountAfterStartup = GatedFile.LoadCount;

        // Act
        await storage.ReconcileAsync();

        // Assert
        Assert.Equal(1, loadCountAfterStartup);
        Assert.Equal(1, GatedFile.LoadCount);
    }

    [Fact]
    public async Task WhenFileIsNamedButUnchanged_ThenItIsNotReloaded()
    {
        // Arrange
        WriteFile("Data.gated");
        var storage = await ConnectAsync();

        // Act
        await storage.ReconcileAsync(Named("Data.gated"));

        // Assert
        Assert.Equal(1, GatedFile.LoadCount);
    }

    [Fact]
    public async Task WhenFileChangesSize_ThenUnnamedPassReloadsIt()
    {
        // Arrange
        WriteFile("Data.gated", "first");
        var storage = await ConnectAsync();
        var file = (GatedFile)storage.Children["Data.gated"];
        WriteFile("Data.gated", "second version");

        // Act
        await storage.ReconcileAsync();

        // Assert
        Assert.Same(file, storage.Children["Data.gated"]);
        Assert.Equal("second version", file.Content);
    }

    [Fact]
    public async Task WhenContentChangesWithSameSizeAndTime_ThenOnlyNamedPassReloadsIt()
    {
        // Arrange
        WriteFile("Data.gated", "first");
        var storage = await ConnectAsync();
        var file = (GatedFile)storage.Children["Data.gated"];
        var originalTime = File.GetLastWriteTimeUtc(GetFullPath("Data.gated"));
        WriteFile("Data.gated", "other");
        File.SetLastWriteTimeUtc(GetFullPath("Data.gated"), originalTime);

        // Act
        await storage.ReconcileAsync();
        var contentAfterUnnamedPass = file.Content;
        await storage.ReconcileAsync(Named("Data.gated"));

        // Assert
        Assert.Equal("first", contentAfterUnnamedPass);
        Assert.Equal("other", file.Content);
    }

    [Fact]
    public async Task WhenEveryFileIsNamed_ThenChangedContentIsReloaded()
    {
        // Arrange
        WriteFile("Data.gated", "first");
        var storage = await ConnectAsync();
        var file = (GatedFile)storage.Children["Data.gated"];
        var originalTime = File.GetLastWriteTimeUtc(GetFullPath("Data.gated"));
        WriteFile("Data.gated", "other");
        File.SetLastWriteTimeUtc(GetFullPath("Data.gated"), originalTime);

        // Act
        await storage.ReconcileAsync(allNamed: true);

        // Assert
        Assert.Equal("other", file.Content);
    }

    [Fact]
    public async Task WhenJsonSubjectFileIsRenamedWithinItsFolder_ThenNewInstanceIsRegisteredUnderTheNewKey()
    {
        // Arrange
        WriteFile("Motor.json", SerializeMotor("Pump"));
        var storage = await ConnectAsync();
        var motor = storage.Children["Motor"];
        File.Move(GetFullPath("Motor.json"), GetFullPath("Engine.json"));

        // Act
        await storage.ReconcileAsync();

        // Assert
        Assert.Equal(["Engine"], storage.Children.Keys);
        var engine = Assert.IsType<Samples.Motor>(storage.Children["Engine"]);
        Assert.NotSame(motor, engine);

        var parent = Assert.Single(engine.TryGetRegisteredSubject()!.Parents);
        Assert.Same(storage, parent.Property.Subject);
        Assert.Equal(nameof(FluentStorageContainer.Children), parent.Property.Name);
        Assert.Equal("Engine", parent.Index);
    }

    [Fact]
    public async Task WhenJsonSubjectFileIsMovedToAnotherFolder_ThenInstanceIsKeptAndNeverDetached()
    {
        // Arrange
        WriteFile("Motor.json", SerializeMotor("Pump"));
        var storage = await ConnectAsync();
        var motor = (Samples.Motor)storage.Children["Motor"];
        var detaches = CountDetachesOf(motor);
        Directory.CreateDirectory(GetFullPath("Devices"));
        File.Move(GetFullPath("Motor.json"), GetFullPath("Devices/Engine.json"));

        // Act
        await storage.ReconcileAsync();

        // Assert
        Assert.Equal(["Devices"], storage.Children.Keys);
        var devices = Assert.IsType<VirtualFolder>(storage.Children["Devices"]);
        Assert.Same(motor, devices.Children["Engine"]);
        Assert.Equal(0, detaches.Count);

        var parent = Assert.Single(motor.TryGetRegisteredSubject()!.Parents);
        Assert.Same(devices, parent.Property.Subject);
        Assert.Equal(nameof(VirtualFolder.Children), parent.Property.Name);
        Assert.Equal("Engine", parent.Index);
    }

    [Fact]
    public async Task WhenFolderOfJsonSubjectIsRenamed_ThenInstanceIsKeptAndNeverDetached()
    {
        // Arrange
        WriteFile("Devices/Motor.json", SerializeMotor("Pump"));
        var storage = await ConnectAsync();
        var motor = (Samples.Motor)((VirtualFolder)storage.Children["Devices"]).Children["Motor"];
        var detaches = CountDetachesOf(motor);
        Directory.Move(GetFullPath("Devices"), GetFullPath("Machines"));

        // Act
        await storage.ReconcileAsync();

        // Assert
        Assert.Equal(["Machines"], storage.Children.Keys);
        var machines = Assert.IsType<VirtualFolder>(storage.Children["Machines"]);
        Assert.Same(motor, machines.Children["Motor"]);
        Assert.Equal(0, detaches.Count);

        var parent = Assert.Single(motor.TryGetRegisteredSubject()!.Parents);
        Assert.Same(machines, parent.Property.Subject);
        Assert.Equal(nameof(VirtualFolder.Children), parent.Property.Name);
        Assert.Equal("Motor", parent.Index);
    }

    [Fact]
    public async Task WhenTwoJsonSubjectsWithSameContentAreRenamed_ThenNewInstancesAreCreated()
    {
        // Arrange
        WriteFile("First.json", SerializeMotor("Same"));
        WriteFile("Second.json", SerializeMotor("Same"));
        var storage = await ConnectAsync();
        var first = storage.Children["First"];
        var second = storage.Children["Second"];
        File.Move(GetFullPath("First.json"), GetFullPath("Third.json"));
        File.Move(GetFullPath("Second.json"), GetFullPath("Fourth.json"));

        // Act
        await storage.ReconcileAsync();

        // Assert
        Assert.Equal(["Fourth", "Third"], storage.Children.Keys.Order());
        Assert.DoesNotContain(storage.Children.Values, subject => ReferenceEquals(subject, first) || ReferenceEquals(subject, second));
    }

    [Fact]
    public async Task WhenJsonSubjectIsRenamedAndEdited_ThenNewInstanceIsCreated()
    {
        // Arrange
        WriteFile("Motor.json", SerializeMotor("Pump"));
        var storage = await ConnectAsync();
        var motor = storage.Children["Motor"];
        File.Delete(GetFullPath("Motor.json"));
        WriteFile("Engine.json", SerializeMotor("Edited"));

        // Act
        await storage.ReconcileAsync();

        // Assert
        var engine = Assert.IsType<Samples.Motor>(storage.Children["Engine"]);
        Assert.NotSame(motor, engine);
        Assert.Equal("Edited", engine.Name);
    }

    [Fact]
    public async Task WhenKeyBecomesFree_ThenBlockedFileIsPlaced()
    {
        // Arrange
        WriteFile("Docs/Readme.md");
        var storage = await ConnectAsync();
        WriteFile("Docs.json", SerializeMotor("Blocked"));
        await storage.ReconcileAsync();
        var folderWhileBlocked = storage.Children["Docs"];
        Directory.Delete(GetFullPath("Docs"), recursive: true);

        // Act
        await storage.ReconcileAsync();

        // Assert
        Assert.IsType<VirtualFolder>(folderWhileBlocked);
        Assert.Equal(["Docs"], storage.Children.Keys);
        Assert.IsType<Samples.Motor>(storage.Children["Docs"]);
    }

    [Fact]
    public async Task WhenNewEntryClashesWithPlacedOne_ThenPlacedOneKeepsItsKey()
    {
        // Arrange
        WriteFile("Docs.json", SerializeMotor("First"));
        var storage = await ConnectAsync();
        var motor = storage.Children["Docs"];
        WriteFile("Docs/Readme.md");

        // Act
        await storage.ReconcileAsync();

        // Assert
        Assert.Equal(["Docs"], storage.Children.Keys);
        Assert.Same(motor, storage.Children["Docs"]);
    }

    [Fact]
    public async Task WhenFileFailsToLoad_ThenItIsRetriedOnlyWhenItChanges()
    {
        // Arrange
        WriteFile("Data.gated", "first");
        GatedFile.FailNextLoad();
        var storage = await ConnectAsync();
        var childrenAfterFailure = storage.Children.Keys.ToList();

        // Act
        await storage.ReconcileAsync();
        var loadCountAfterIdlePass = GatedFile.LoadCount;
        WriteFile("Data.gated", "second version");
        await storage.ReconcileAsync();

        // Assert
        Assert.Empty(childrenAfterFailure);
        Assert.Equal(1, loadCountAfterIdlePass);
        var file = Assert.IsType<GatedFile>(storage.Children["Data.gated"]);
        Assert.Equal("second version", file.Content);
    }

    [Fact]
    public async Task WhenFileIsRenamedToIgnoredName_ThenItsSubjectIsRemoved()
    {
        // Arrange
        WriteFile("Home.md");
        var storage = await ConnectAsync();
        File.Move(GetFullPath("Home.md"), GetFullPath("Home.md~"));

        // Act
        await storage.ReconcileAsync(Named("Home.md", "Home.md~"));

        // Assert
        Assert.Empty(storage.Children);
    }

    [Fact]
    public async Task WhenFilesDifferOnlyInCasing_ThenBothArePlaced()
    {
        // Arrange
        // In memory, because not every volume can hold both files.
        var storage = await ConnectAsync(configure: container => container.StorageType = "inmemory");
        await WriteBlobAsync(storage, "readme.md", "lower");
        await WriteBlobAsync(storage, "README.md", "upper");

        // Act
        await storage.ReconcileAsync();

        // Assert
        Assert.Equal(["README.md", "readme.md"], storage.Children.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("lower", Assert.IsType<MarkdownFile>(storage.Children["readme.md"]).Content);
        Assert.Equal("upper", Assert.IsType<MarkdownFile>(storage.Children["README.md"]).Content);
    }

    [Fact]
    public async Task WhenJsonFileDescribesNoSubject_ThenItIsPlacedAsJsonFileUnderItsFullName()
    {
        // Arrange
        WriteFile("Data.json", """{ "value": 1 }""");

        // Act
        var storage = await ConnectAsync();

        // Assert
        Assert.Equal(["Data.json"], storage.Children.Keys);
        Assert.IsType<JsonFile>(storage.Children["Data.json"]);
    }

    [Fact]
    public async Task WhenSubjectThrowsCancellationOnLoad_ThenFileIsRecordedAsFailedAndPassCompletes()
    {
        // Arrange
        var storage = await ConnectAsync();
        WriteFile("Data.gated");
        WriteFile("Notes.md");
        GatedFile.FailNextLoad(new OperationCanceledException());

        // Act
        await storage.ReconcileAsync();
        var childrenAfterFailure = storage.Children.Keys.ToList();
        await storage.ReconcileAsync();

        // Assert
        Assert.Equal(["Notes.md"], childrenAfterFailure);
        Assert.Equal(["Notes.md"], storage.Children.Keys);
        Assert.Equal(1, GatedFile.LoadCount);
        Assert.Equal(StorageStatus.Connected, storage.Status);
    }

    [Fact]
    public async Task WhenStorageReconnectsWhilePassRuns_ThenOnlyTheNewConnectionChangesTheTree()
    {
        // Arrange
        var otherDirectory = CreateTemporaryDirectory();
        File.WriteAllText(Path.Combine(otherDirectory.FullName, "New.gated"), "content");

        WriteFile("Old.md");
        var storage = await ConnectAsync();
        WriteFile("Slow.gated");
        var gateOfOldPass = GatedFile.PauseNextLoad();
        var gateOfNewPass = GatedFile.PauseNextLoad();
        var oldPass = storage.ReconcileAsync();
        await gateOfOldPass.WhenReachedAsync();
        storage.ConnectionString = otherDirectory.FullName;

        // Act
        var reconnect = storage.ApplyConfigurationAsync(CancellationToken.None);
        var reconnectWasWaiting = !reconnect.IsCompleted;
        gateOfOldPass.Release();
        await oldPass;
        await gateOfNewPass.WhenReachedAsync();
        var childrenAfterOldPass = storage.Children.Keys.ToList();
        gateOfNewPass.Release();
        await reconnect;

        // Assert
        Assert.True(reconnectWasWaiting);
        Assert.Equal(["Old.md"], childrenAfterOldPass);
        Assert.Equal(["New.gated"], storage.Children.Keys);
        Assert.Equal(StorageStatus.Connected, storage.Status);
    }

    [Fact]
    public async Task WhenStorageIsDisposedWhilePassRuns_ThenPassChangesNeitherTreeNorStatus()
    {
        // Arrange
        WriteFile("Notes.md");
        var storage = await ConnectAsync();
        WriteFile("Slow.gated");
        var gate = GatedFile.PauseNextLoad();
        var pass = storage.ReconcileAsync();
        await gate.WhenReachedAsync();

        // Act
        storage.Dispose();
        gate.Release();
        await pass;

        // Assert
        Assert.Equal(["Notes.md"], storage.Children.Keys);
        Assert.Equal(StorageStatus.Disconnected, storage.Status);
    }

    [Fact]
    public async Task WhenStorageIsDisposedWhileItReconnects_ThenReconnectFailsAndNothingIsLoaded()
    {
        // Arrange
        WriteFile("Notes.md");
        var storage = await ConnectAsync();
        WriteFile("Slow.gated");
        var gate = GatedFile.PauseNextLoad();
        var pass = storage.ReconcileAsync();
        await gate.WhenReachedAsync();
        var reconnect = storage.ApplyConfigurationAsync(CancellationToken.None);

        // Act
        storage.Dispose();
        gate.Release();
        await pass;

        // Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(() => reconnect);
        Assert.Equal(["Notes.md"], storage.Children.Keys);
        Assert.Equal(StorageStatus.Disconnected, storage.Status);
    }

    [Fact]
    public async Task WhenStorageIsDisposedWhileSecondReconnectWaits_ThenThatReconnectFailsToo()
    {
        // Arrange
        WriteFile("Notes.md");
        var storage = await ConnectAsync();
        WriteFile("Slow.gated");
        var gate = GatedFile.PauseNextLoad();
        var pass = storage.ReconcileAsync();
        await gate.WhenReachedAsync();
        var reconnect = storage.ApplyConfigurationAsync(CancellationToken.None);
        var waitingReconnect = storage.ApplyConfigurationAsync(CancellationToken.None);

        // Act
        storage.Dispose();
        gate.Release();
        await pass;

        // Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(() => reconnect);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => waitingReconnect);
        Assert.Equal(["Notes.md"], storage.Children.Keys);
        Assert.Equal(StorageStatus.Disconnected, storage.Status);
    }

    [Fact]
    public async Task WhenReconnectIsCancelledWhilePreviousWorkRuns_ThenItFailsAndLaterReconnectWaitsAgain()
    {
        // Arrange
        WriteFile("Notes.md");
        var storage = await ConnectAsync();
        WriteFile("Slow.gated");
        var gate = GatedFile.PauseNextLoad();
        var pass = storage.ReconcileAsync();
        await gate.WhenReachedAsync();
        using var cancellation = new CancellationTokenSource();
        var reconnect = storage.ConnectAsync(cancellation.Token);

        // Act
        await cancellation.CancelAsync();
        var exception = await Record.ExceptionAsync(() => reconnect);
        var previousWorkWasRunning = !pass.IsCompleted;
        var statusAfterCancelledReconnect = storage.Status;
        var laterReconnect = storage.ConnectAsync(CancellationToken.None);
        var laterReconnectWasWaiting = !laterReconnect.IsCompleted;
        gate.Release();
        await pass;
        await laterReconnect;

        // Assert
        Assert.IsType<OperationCanceledException>(exception, exactMatch: false);
        Assert.True(previousWorkWasRunning);
        Assert.Equal(StorageStatus.Error, statusAfterCancelledReconnect);
        Assert.True(laterReconnectWasWaiting);
        Assert.Equal(StorageStatus.Connected, storage.Status);
        Assert.Equal(["Notes.md", "Slow.gated"], storage.Children.Keys.Order());
    }

    [Fact]
    public async Task WhenStorageIsDisposed_ThenConfigurationIsNotWritten()
    {
        // Arrange
        WriteFile("Motor.json", SerializeMotor("Pump"));
        var storage = await ConnectAsync();
        var motor = (Samples.Motor)storage.Children["Motor"];
        var contentBeforeDispose = File.ReadAllText(GetFullPath("Motor.json"));
        motor.Name = "Changed";
        storage.Dispose();

        // Act
        var isWritten = await storage.WriteConfigurationAsync(motor, CancellationToken.None);

        // Assert
        Assert.False(isWritten);
        Assert.Equal(contentBeforeDispose, File.ReadAllText(GetFullPath("Motor.json")));
    }

    [Fact]
    public async Task WhenFolderIsDeletedAsSubject_ThenItThrowsAndFolderIsKept()
    {
        // Arrange
        WriteFile("Docs/Readme.md");
        var storage = await ConnectAsync();
        var docs = storage.Children["Docs"];

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            storage.DeleteSubjectAsync(docs, CancellationToken.None));

        Assert.Same(docs, storage.Children["Docs"]);
        Assert.True(File.Exists(GetFullPath("Docs/Readme.md")));
    }

    [Fact]
    public async Task WhenUiOperationArrivesDuringPass_ThenItRunsAfterThePass()
    {
        // Arrange
        var storage = await ConnectAsync();
        WriteFile("Slow.gated");
        var gate = GatedFile.PauseNextLoad();
        var pass = storage.ReconcileAsync();
        await gate.WhenReachedAsync();

        // Act
        var add = storage.AddSubjectAsync("Motor.json", CreateMotor(), CancellationToken.None);
        var addWasWaiting = !add.IsCompleted;
        gate.Release();
        await Task.WhenAll(pass, add);

        // Assert
        Assert.True(addWasWaiting);
        Assert.Equal(["Motor", "Slow.gated"], storage.Children.Keys.Order());
    }

    [Fact]
    public async Task WhenConfigurationIsWritten_ThenNamedPassDoesNotReloadTheSubject()
    {
        // Arrange
        WriteFile("Motor.json", SerializeMotor("From file"));
        var storage = await ConnectAsync();
        var motor = (Samples.Motor)storage.Children["Motor"];
        motor.Name = "Saved";
        await storage.WriteConfigurationAsync(motor, CancellationToken.None);
        motor.Name = "Only in memory";

        // Act
        await storage.ReconcileAsync(Named("Motor.json"));

        // Assert
        Assert.Contains("Saved", File.ReadAllText(GetFullPath("Motor.json")));
        Assert.Equal("Only in memory", motor.Name);
    }

    [Fact]
    public async Task WhenBlobIsWritten_ThenSubjectReloadsOnceAndNamedPassDoesNotReloadIt()
    {
        // Arrange
        WriteFile("Data.gated", "first");
        var storage = await ConnectAsync();
        var file = (GatedFile)storage.Children["Data.gated"];

        // Act
        await WriteBlobAsync(storage, "Data.gated", "second version");
        var loadCountAfterWrite = GatedFile.LoadCount;
        await storage.ReconcileAsync(Named("Data.gated"));

        // Assert
        Assert.Equal("second version", file.Content);
        Assert.Equal(2, loadCountAfterWrite);
        Assert.Equal(2, GatedFile.LoadCount);
    }

    [Fact]
    public async Task WhenSubjectIsDeleted_ThenFileAndSubjectAreGone()
    {
        // Arrange
        WriteFile("Docs/Motor.json", SerializeMotor("Pump"));
        var storage = await ConnectAsync();
        var docs = (VirtualFolder)storage.Children["Docs"];

        // Act
        await storage.DeleteSubjectAsync(docs.Children["Motor"], CancellationToken.None);

        // Assert
        Assert.Empty(docs.Children);
        Assert.False(File.Exists(GetFullPath("Docs/Motor.json")));
    }

    [Fact]
    public async Task WhenPassAttachesSubjects_ThenLifecycleHandlersRunWithoutTheFlowOfThePass()
    {
        // Arrange
        var storage = await ConnectAsync();
        var recorder = new LifecycleFlowRecorder();
        Context!.AddService<ILifecycleHandler>(recorder);
        WriteFile("Motor.json", SerializeMotor("Pump"));
        WriteFile("Notes.md");

        // Act
        await storage.ReconcileAsync();

        // Assert
        var changes = recorder.Changes;
        Assert.Contains(changes, change => change is { Subject: Samples.Motor, IsContextAttach: true });
        Assert.Contains(changes, change => change is { Subject: Samples.Motor, IsContextAttach: false });
        Assert.Contains(changes, change => change is { Subject: MarkdownFile, IsContextAttach: true });
        Assert.All(changes, change => Assert.True(change.IsFlowSuppressed, $"{change.Subject.GetType().Name}, context attach: {change.IsContextAttach}"));
    }

    [Fact]
    public async Task WhenSubjectFailsToRefreshAfterBlobIsWritten_ThenWriteSucceedsAndNamedPassLoadsItAgain()
    {
        // Arrange
        WriteFile("Data.gated", "first");
        var storage = await ConnectAsync();
        var file = (GatedFile)storage.Children["Data.gated"];
        GatedFile.FailNextLoad();

        // Act
        await WriteBlobAsync(storage, "Data.gated", "second version");
        await storage.ReconcileAsync();
        var loadCountAfterUnnamedPass = GatedFile.LoadCount;
        await storage.ReconcileAsync(Named("Data.gated"));

        // Assert
        Assert.Equal("second version", File.ReadAllText(GetFullPath("Data.gated")));
        Assert.Equal(2, loadCountAfterUnnamedPass);
        Assert.Equal(3, GatedFile.LoadCount);
        Assert.Equal("second version", file.Content);
    }

    [Fact]
    public async Task WhenPlainFileFailsToRefreshAfterBlobIsWritten_ThenNextPassRefreshesIt()
    {
        // Arrange
        WriteFile("Data.bin", "first");
        var storage = await ConnectAsync();
        var file = (GenericFile)storage.Children["Data.bin"];
        var failingWrite = new FailingWrite(nameof(GenericFile.FileSize));
        Context!.AddService<IWriteInterceptor>(failingWrite);
        failingWrite.FailNext();

        // Act
        await WriteBlobAsync(storage, "Data.bin", "second version");
        var sizeAfterWrite = file.FileSize;
        await storage.ReconcileAsync();

        // Assert
        Assert.Equal("first".Length, sizeAfterWrite);
        Assert.Equal("second version".Length, file.FileSize);
    }

    [Fact]
    public async Task WhenFileChangesWhileItsSubjectLoads_ThenNextPassReloadsIt()
    {
        // Arrange
        var storage = await ConnectAsync();
        WriteFile("Data.gated", "first");
        var gate = GatedFile.PauseNextLoad();
        var pass = storage.ReconcileAsync();
        await gate.WhenReachedAsync();
        WriteFile("Data.gated", "second version");
        gate.Release();
        await pass;
        var file = (GatedFile)storage.Children["Data.gated"];
        var contentAfterFirstPass = file.Content;

        // Act
        await storage.ReconcileAsync(Named("Data.gated"));

        // Assert
        Assert.Equal("first", contentAfterFirstPass);
        Assert.Equal("second version", file.Content);
    }

    private static async Task WriteBlobAsync(FluentStorageContainer storage, string path, string content)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        await storage.WriteBlobAsync(path, stream, CancellationToken.None);
    }

    private DetachCounter CountDetachesOf(IInterceptorSubject subject)
    {
        var counter = new DetachCounter(subject);
        Context!.AddService<ILifecycleHandler>(counter);
        return counter;
    }

    private sealed class LifecycleFlowRecorder : ILifecycleHandler
    {
        private readonly List<(IInterceptorSubject Subject, bool IsContextAttach, bool IsFlowSuppressed)> _changes = [];

        public IReadOnlyList<(IInterceptorSubject Subject, bool IsContextAttach, bool IsFlowSuppressed)> Changes
        {
            get
            {
                lock (_changes)
                {
                    return _changes.ToList();
                }
            }
        }

        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            if (change.Subject is Samples.Motor or MarkdownFile)
            {
                lock (_changes)
                {
                    _changes.Add((change.Subject, change.IsContextAttach, ExecutionContext.IsFlowSuppressed()));
                }
            }
        }
    }

    private sealed class FailingWrite(string propertyName) : IWriteInterceptor
    {
        private int _failNext;

        public void FailNext() => Volatile.Write(ref _failNext, 1);

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            if (context.Property.Name == propertyName && Interlocked.Exchange(ref _failNext, 0) == 1)
            {
                throw new InvalidOperationException("The write was made to fail.");
            }

            next(ref context);
        }
    }

    private sealed class DetachCounter(IInterceptorSubject subject) : ILifecycleHandler
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            if (change.IsContextDetach && ReferenceEquals(change.Subject, subject))
            {
                Interlocked.Increment(ref _count);
            }
        }
    }
}
