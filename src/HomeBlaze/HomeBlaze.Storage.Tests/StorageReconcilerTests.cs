using System.Text;
using FluentStorage.Blobs;
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
    public async Task WhenFilesAreLoaded_ThenTheirFullPathStartsWithSlash()
    {
        // Arrange
        WriteFile("Docs/Readme.md", "# Readme");
        WriteFile("Docs/Data.bin");
        WriteFile("Docs/Plain.json", "{}");

        // Act
        var storage = await ConnectAsync();

        // Assert
        var docs = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);
        Assert.Equal("/Docs/Readme.md", Assert.IsType<MarkdownFile>(docs.Children["Readme.md"]).FullPath);
        Assert.Equal("/Docs/Data.bin", Assert.IsType<GenericFile>(docs.Children["Data.bin"]).FullPath);
        Assert.Equal("/Docs/Plain.json", Assert.IsType<JsonFile>(docs.Children["Plain.json"]).FullPath);
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

    [Theory]
    [InlineData("{")]
    [InlineData("")]
    [InlineData("""{ "name": "Pump" }""")]
    [InlineData("""{ "$type": "Unknown.Type", "name": "Pump" }""")]
    public async Task WhenJsonFileBecomesValidSubject_ThenSubjectReplacesTheJsonFile(string contentBefore)
    {
        // Arrange
        WriteFile("Motor.json", contentBefore);
        var storage = await ConnectAsync();
        var childrenBefore = storage.Children.ToDictionary();
        WriteFile("Motor.json", SerializeMotor("Pump"));

        // Act
        await storage.ReconcileAsync();

        // Assert
        Assert.IsType<JsonFile>(Assert.Single(childrenBefore, child => child.Key == "Motor.json").Value);
        Assert.Equal(["Motor"], storage.Children.Keys);
        Assert.Equal("Pump", Assert.IsType<Samples.Motor>(storage.Children["Motor"]).Name);
    }

    [Fact]
    public async Task WhenTypeOfJsonSubjectChanges_ThenSubjectOfTheNewTypeReplacesIt()
    {
        // Arrange
        WriteFile("Device.json", SerializeMotor("Pump"));
        var storage = await ConnectAsync();
        var motor = Assert.IsType<Samples.Motor>(storage.Children["Device"]);
        var detaches = CountDetachesOf(motor);
        WriteFile("Device.json", CallbackSubject.Serialize("Valve"));

        // Act
        await storage.ReconcileAsync();

        // Assert
        Assert.Equal(["Device"], storage.Children.Keys);
        Assert.Equal("Valve", Assert.IsType<CallbackSubject>(storage.Children["Device"]).Name);
        Assert.Equal(1, detaches.Count);
        Assert.Equal("Pump", motor.Name);
    }

    [Fact]
    public async Task WhenJsonSubjectFileLosesItsType_ThenJsonFileReplacesTheSubject()
    {
        // Arrange
        WriteFile("Motor.json", SerializeMotor("Pump"));
        var storage = await ConnectAsync();
        var motor = Assert.IsType<Samples.Motor>(storage.Children["Motor"]);
        var detaches = CountDetachesOf(motor);
        WriteFile("Motor.json", """{ "name": "Pump" }""");

        // Act
        await storage.ReconcileAsync();

        // Assert
        Assert.Equal(["Motor.json"], storage.Children.Keys);
        Assert.IsType<JsonFile>(storage.Children["Motor.json"]);
        Assert.Equal(1, detaches.Count);
    }

    [Fact]
    public async Task WhenJsonSubjectFileIsInvalidForAWhile_ThenSubjectKeepsItsInstance()
    {
        // Arrange
        WriteFile("Motor.json", SerializeMotor("Pump"));
        var storage = await ConnectAsync();
        var motor = Assert.IsType<Samples.Motor>(storage.Children["Motor"]);
        var detaches = CountDetachesOf(motor);
        WriteFile("Motor.json", "{");

        // Act
        await storage.ReconcileAsync();
        var childrenWhileInvalid = storage.Children.ToDictionary();
        var nameWhileInvalid = motor.Name;
        WriteFile("Motor.json", SerializeMotor("Pump, changed"));
        await storage.ReconcileAsync();

        // Assert
        Assert.Same(motor, Assert.Single(childrenWhileInvalid, child => child.Key == "Motor").Value);
        Assert.Equal("Pump", nameWhileInvalid);
        Assert.Same(motor, storage.Children["Motor"]);
        Assert.Equal("Pump, changed", motor.Name);
        Assert.Equal(0, detaches.Count);
    }

    [Fact]
    public async Task WhenPlainJsonFileChanges_ThenItsInstanceIsKeptWithTheNewSize()
    {
        // Arrange
        WriteFile("Values.json", """{ "value": 1 }""");
        var storage = await ConnectAsync();
        var file = Assert.IsType<JsonFile>(storage.Children["Values.json"]);
        WriteFile("Values.json", """{ "value": 22 }""");

        // Act
        await storage.ReconcileAsync();

        // Assert
        Assert.Same(file, storage.Children["Values.json"]);
        Assert.Equal("""{ "value": 22 }""".Length, file.FileSize);
    }

    [Fact]
    public async Task WhenPlainJsonFileIsGivenATypeThroughBlobWrite_ThenSubjectReplacesTheJsonFile()
    {
        // Arrange
        WriteFile("Motor.json", """{ "name": "Pump" }""");
        var storage = await ConnectAsync();
        var childrenBefore = storage.Children.Keys.ToList();

        // Act
        await WriteBlobAsync(storage, "Motor.json", SerializeMotor("Pump"));

        // Assert
        Assert.Equal(["Motor.json"], childrenBefore);
        Assert.Equal(["Motor"], storage.Children.Keys);
        Assert.Equal("Pump", Assert.IsType<Samples.Motor>(storage.Children["Motor"]).Name);
    }

    [Fact]
    public async Task WhenKeyOfBlockedFolderBecomesFree_ThenItsFilesAreLoadedInTheSamePass()
    {
        // Arrange
        WriteFile("Docs.json", SerializeMotor("Blocker"));
        var storage = await ConnectAsync();
        WriteFile("Docs/Readme.md");
        WriteFile("Docs/Guides/Setup.md");
        await storage.ReconcileAsync();
        var childWhileBlocked = storage.Children["Docs"];
        File.Delete(GetFullPath("Docs.json"));

        // Act
        await storage.ReconcileAsync();

        // Assert
        Assert.IsType<Samples.Motor>(childWhileBlocked);
        var docs = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);
        Assert.Equal(["Guides", "Readme.md"], docs.Children.Keys.Order());
        Assert.Equal(["Setup.md"], Assert.IsType<VirtualFolder>(docs.Children["Guides"]).Children.Keys);
    }

    [Fact]
    public async Task WhenFolderStaysBlocked_ThenItsFilesAreNotLoadedAgain()
    {
        // Arrange
        WriteFile("Docs.json", SerializeMotor("Blocker"));
        var storage = await ConnectAsync();
        WriteFile("Docs/Data.gated");
        await storage.ReconcileAsync();
        var loadCountAfterFirstPass = GatedFile.LoadCount;

        // Act
        await storage.ReconcileAsync();

        // Assert
        Assert.Equal(1, loadCountAfterFirstPass);
        Assert.Equal(1, GatedFile.LoadCount);
        Assert.IsType<Samples.Motor>(storage.Children["Docs"]);
    }

    [Fact]
    public async Task WhenFileFailsToLoadWithIOException_ThenNextPassTriesAgain()
    {
        // Arrange
        WriteFile("Data.gated", "first");
        GatedFile.FailNextLoad(new IOException("The file is in use."));
        var storage = await ConnectAsync();
        var childrenAfterFailure = storage.Children.Keys.ToList();

        // Act
        await storage.ReconcileAsync();

        // Assert
        Assert.Empty(childrenAfterFailure);
        Assert.Equal(2, GatedFile.LoadCount);
        Assert.Equal("first", Assert.IsType<GatedFile>(storage.Children["Data.gated"]).Content);
    }

    [Fact]
    public async Task WhenChangedFileFailsToReloadWithIOException_ThenNextPassTriesAgain()
    {
        // Arrange
        WriteFile("Data.gated", "first");
        var storage = await ConnectAsync();
        var file = (GatedFile)storage.Children["Data.gated"];
        WriteFile("Data.gated", "second version");
        GatedFile.FailNextLoad(new IOException("The file is in use."));

        // Act
        await storage.ReconcileAsync();
        var contentAfterFailure = file.Content;
        await storage.ReconcileAsync();

        // Assert
        Assert.Equal("first", contentAfterFailure);
        Assert.Equal("second version", file.Content);
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
    public async Task WhenStorageReconnectsToEmptyDirectory_ThenSubjectsOfPreviousConnectionAreRemoved()
    {
        // Arrange
        var emptyDirectory = CreateTemporaryDirectory();
        WriteFile("Old.md");
        WriteFile("Docs/Readme.md");
        var storage = await ConnectAsync();
        var childrenOfPreviousConnection = storage.Children.Keys.Order().ToList();
        storage.ConnectionString = emptyDirectory.FullName;

        // Act
        await storage.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        Assert.Equal(["Docs", "Old.md"], childrenOfPreviousConnection);
        Assert.Empty(storage.Children);
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
        var reconnect = storage.ConnectAsync(CancellationToken.None);

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
        var reconnect = storage.ConnectAsync(CancellationToken.None);
        var waitingReconnect = storage.ConnectAsync(CancellationToken.None);

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
    public async Task WhenSubjectFailsToRefreshAfterBlobIsWritten_ThenWriteSucceedsAndNextPassLoadsItAgain()
    {
        // Arrange
        WriteFile("Data.gated", "first");
        var storage = await ConnectAsync();
        var file = (GatedFile)storage.Children["Data.gated"];
        GatedFile.FailNextLoad();

        // Act
        await WriteBlobAsync(storage, "Data.gated", "second version");
        var contentAfterWrite = file.Content;
        var loadCountAfterWrite = GatedFile.LoadCount;
        await storage.ReconcileAsync();

        // Assert
        Assert.Equal("second version", File.ReadAllText(GetFullPath("Data.gated")));
        Assert.Equal("first", contentAfterWrite);
        Assert.Equal(2, loadCountAfterWrite);
        Assert.Equal(3, GatedFile.LoadCount);
        Assert.Equal("second version", file.Content);
    }

    [Fact]
    public async Task WhenMarkdownFileFailsToRefreshAfterBlobIsWritten_ThenWriteSucceedsAndNextPassLoadsItAgain()
    {
        // Arrange
        WriteFile("Notes.md", "first");
        var storage = await ConnectAsync();
        var file = (MarkdownFile)storage.Children["Notes.md"];
        var failingWrite = new FailingWrite(nameof(MarkdownFile.Content));
        Context!.AddService<IWriteInterceptor>(failingWrite);
        failingWrite.FailNext();

        // Act
        await WriteBlobAsync(storage, "Notes.md", "second version");
        var contentAfterWrite = file.Content;
        await storage.ReconcileAsync();

        // Assert
        Assert.Equal("second version", File.ReadAllText(GetFullPath("Notes.md")));
        Assert.Equal("first", contentAfterWrite);
        Assert.Equal("second version", file.Content);
    }

    [Fact]
    public async Task WhenConfigurationFileIsWrittenAsBlob_ThenItsSubjectTakesTheContentAndNamedPassDoesNotReloadIt()
    {
        // Arrange
        WriteFile("Motor.json", SerializeMotor("From file"));
        var storage = await ConnectAsync();
        var motor = (Samples.Motor)storage.Children["Motor"];

        // Act
        await WriteBlobAsync(storage, "Motor.json", SerializeMotor("Written"));
        var nameAfterWrite = motor.Name;
        motor.Name = "Only in memory";
        await storage.ReconcileAsync(Named("Motor.json"));

        // Assert
        Assert.Equal("Written", nameAfterWrite);
        Assert.Same(motor, storage.Children["Motor"]);
        Assert.Equal("Only in memory", motor.Name);
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

    [Fact]
    public async Task WhenPlainFilesAreLoadedAndRefreshed_ThenNoMetadataCallIsMade()
    {
        // Arrange
        PausableBlobStorage? client = null;
        WriteFile("Data.bin", "first");
        WriteFile("Values.json", """{ "value": 1 }""");
        var storage = await ConnectAsync(configure: container =>
            container.ClientDecorator = inner => client = new PausableBlobStorage(inner));
        WriteFile("Data.bin", "second version");
        WriteFile("Values.json", """{ "value": 22 }""");

        // Act
        await storage.ReconcileAsync();

        // Assert
        Assert.Equal(0, client!.GetCallCount(nameof(IBlobStorage.GetBlobsAsync)));
        Assert.Equal("second version".Length, Assert.IsType<GenericFile>(storage.Children["Data.bin"]).FileSize);
        Assert.Equal("""{ "value": 22 }""".Length, Assert.IsType<JsonFile>(storage.Children["Values.json"]).FileSize);
    }

    [Theory]
    [InlineData("Data.bin")]
    [InlineData("Values.json")]
    [InlineData("Notes.md")]
    [InlineData("Data.gated")]
    public async Task WhenFileIsLoaded_ThenItsSizeAndUtcModificationTimeAreSet(string path)
    {
        // Arrange
        WriteFile(path, "12345");

        // Act
        var storage = await ConnectAsync();

        // Assert
        var file = Assert.IsType<IStorageFile>(storage.Children[path], exactMatch: false);
        Assert.Equal(5, file.FileSize);
        Assert.Equal(DateTimeKind.Utc, file.LastModified.Kind);
        Assert.Equal(File.GetLastWriteTimeUtc(GetFullPath(path)), file.LastModified);
    }

    [Theory]
    [InlineData("Data.bin")]
    [InlineData("Values.json")]
    [InlineData("Notes.md")]
    [InlineData("Data.gated")]
    public async Task WhenOnlyModificationTimeChanges_ThenSubjectShowsItWithoutBeingReloaded(string path)
    {
        // Arrange
        var modified = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        WriteFile(path, "12345");
        var storage = await ConnectAsync();
        var file = Assert.IsType<IStorageFile>(storage.Children[path], exactMatch: false);
        var loadCountAfterStartup = GatedFile.LoadCount;
        File.SetLastWriteTimeUtc(GetFullPath(path), modified);

        // Act
        await storage.ReconcileAsync();

        // Assert
        Assert.Same(file, storage.Children[path]);
        Assert.Equal(modified, file.LastModified);
        Assert.Equal(DateTimeKind.Utc, file.LastModified.Kind);
        Assert.Equal(loadCountAfterStartup, GatedFile.LoadCount);
    }

    private static async Task WriteBlobAsync(FluentStorageContainer storage, string path, string content)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        await storage.WriteBlobAsync(path, stream, CancellationToken.None);
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
}
