using HomeBlaze.Storage.Files;
using Namotion.Interceptor;
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
        var file = Assert.IsType<GatedFile>(storage.Children["Data.gated"]);
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
        var file = Assert.IsType<GatedFile>(storage.Children["Data.gated"]);
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
        var file = Assert.IsType<GatedFile>(storage.Children["Data.gated"]);
        var originalTime = File.GetLastWriteTimeUtc(GetFullPath("Data.gated"));
        WriteFile("Data.gated", "other");
        File.SetLastWriteTimeUtc(GetFullPath("Data.gated"), originalTime);

        // Act
        await storage.ReconcileAsync(allNamed: true);

        // Assert
        Assert.Equal("other", file.Content);
    }

    [Fact]
    public async Task WhenJsonSubjectFileIsRenamed_ThenInstanceIsKeptAndNeverDetached()
    {
        // Arrange
        WriteFile("Motor.json", SerializeMotor("Pump"));
        var storage = await ConnectAsync();
        var motor = Assert.IsType<Samples.Motor>(storage.Children["Motor"]);
        var detaches = CountDetachesOf(motor);
        File.Move(GetFullPath("Motor.json"), GetFullPath("Engine.json"));

        // Act
        await storage.ReconcileAsync();

        // Assert
        Assert.Equal(["Engine"], storage.Children.Keys);
        Assert.Same(motor, storage.Children["Engine"]);
        Assert.Equal(0, detaches.Count);
    }

    [Fact]
    public async Task WhenFolderOfJsonSubjectIsRenamed_ThenInstanceIsKeptAndNeverDetached()
    {
        // Arrange
        WriteFile("Devices/Motor.json", SerializeMotor("Pump"));
        var storage = await ConnectAsync();
        var motor = Assert.IsType<Samples.Motor>(((VirtualFolder)storage.Children["Devices"]).Children["Motor"]);
        var detaches = CountDetachesOf(motor);
        Directory.Move(GetFullPath("Devices"), GetFullPath("Machines"));

        // Act
        await storage.ReconcileAsync();

        // Assert
        Assert.Equal(["Machines"], storage.Children.Keys);
        Assert.Same(motor, ((VirtualFolder)storage.Children["Machines"]).Children["Motor"]);
        Assert.Equal(0, detaches.Count);
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
        WriteFile("readme.md", "lower");
        if (File.Exists(GetFullPath("README.md")))
        {
            // The volume ignores case, so the second file cannot exist next to the first one.
            return;
        }

        WriteFile("README.md", "upper");

        // Act
        var storage = await ConnectAsync();

        // Assert
        Assert.Equal(["README.md", "readme.md"], storage.Children.Keys.Order(StringComparer.Ordinal));
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
        var motor = Assert.IsType<Samples.Motor>(storage.Children["Motor"]);
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
        var file = Assert.IsType<GatedFile>(storage.Children["Data.gated"]);

        // Act
        using (var content = new MemoryStream("second version"u8.ToArray()))
        {
            await storage.WriteBlobAsync("Data.gated", content, CancellationToken.None);
        }

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
        var docs = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);

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

        // Assert: a JSON subject enters the context when it is created, a file subject when it is assigned.
        var changes = recorder.Changes;
        Assert.Contains(changes, change => change is { Subject: Samples.Motor, IsContextAttach: true });
        Assert.Contains(changes, change => change is { Subject: Samples.Motor, IsContextAttach: false });
        Assert.Contains(changes, change => change is { Subject: MarkdownFile, IsContextAttach: true });
        Assert.All(changes, change => Assert.True(change.IsFlowSuppressed, $"{change.Subject.GetType().Name}, context attach: {change.IsContextAttach}"));
    }

    [Fact]
    public async Task WhenSubjectFailsToRefreshAfterBlobIsWritten_ThenWriteSucceedsAndIsRecorded()
    {
        // Arrange
        WriteFile("Data.gated", "first");
        var storage = await ConnectAsync();
        GatedFile.FailNextLoad();

        // Act
        using (var content = new MemoryStream("second version"u8.ToArray()))
        {
            await storage.WriteBlobAsync("Data.gated", content, CancellationToken.None);
        }

        var loadCountAfterWrite = GatedFile.LoadCount;
        await storage.ReconcileAsync(Named("Data.gated"));

        // Assert
        Assert.Equal("second version", File.ReadAllText(GetFullPath("Data.gated")));
        Assert.Equal(2, loadCountAfterWrite);
        Assert.Equal(2, GatedFile.LoadCount);
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
        var file = Assert.IsType<GatedFile>(storage.Children["Data.gated"]);
        var contentAfterFirstPass = file.Content;

        // Act
        await storage.ReconcileAsync(Named("Data.gated"));

        // Assert
        Assert.Equal("first", contentAfterFirstPass);
        Assert.Equal("second version", file.Content);
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
