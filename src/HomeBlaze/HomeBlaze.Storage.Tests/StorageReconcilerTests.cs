using System.Collections.Concurrent;
using System.Text;
using FluentStorage.Blobs;
using HomeBlaze.Storage.Abstractions;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Internal;
using Namotion.Interceptor;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Storage.Tests;

public class StorageReconcilerTests : StorageTestBase
{
    // Work that waits for something that never happens never completes, so the wait for it ends the test instead.
    private static readonly TimeSpan DeadlockTimeout = TimeSpan.FromSeconds(30);

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
    public async Task WhenFilesWereReloadedOnce_ThenLaterPassesOpenNoFile()
    {
        // Arrange
        PausableBlobStorage? client = null;
        WriteFile("Notes.md", "first");
        WriteFile("Data.gated", "first");
        WriteFile("Motor.json", SerializeMotor("Pump"));
        var storage = await ConnectAsync(configure: container =>
            container.ClientDecorator = inner => client = new PausableBlobStorage(inner));
        WriteFile("Notes.md", "second version");
        WriteFile("Data.gated", "second version");
        WriteFile("Motor.json", SerializeMotor("Pump, changed"));
        await storage.ReconcileAsync();
        var openCountAfterReload = client!.GetCallCount(nameof(IBlobStorage.OpenReadAsync));

        // Act
        await storage.ReconcileAsync();
        await storage.ReconcileAsync();

        // Assert
        Assert.Equal(openCountAfterReload, client.GetCallCount(nameof(IBlobStorage.OpenReadAsync)));
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
    public async Task WhenTwoJsonSubjectsWithSameContentMoveToOtherFolders_ThenNeitherInstanceIsKept()
    {
        // Arrange: into other folders, where one subject with that content alone would keep its instance.
        WriteFile("A/Pump.json", SerializeMotor("Same"));
        WriteFile("B/Pump.json", SerializeMotor("Same"));
        var storage = await ConnectAsync();
        var first = ((VirtualFolder)storage.Children["A"]).Children["Pump"];
        var second = ((VirtualFolder)storage.Children["B"]).Children["Pump"];
        Directory.CreateDirectory(GetFullPath("C"));
        Directory.CreateDirectory(GetFullPath("D"));
        File.Move(GetFullPath("A/Pump.json"), GetFullPath("C/Pump.json"));
        File.Move(GetFullPath("B/Pump.json"), GetFullPath("D/Pump.json"));

        // Act
        await storage.ReconcileAsync();

        // Assert
        IInterceptorSubject[] moved =
        [
            ((VirtualFolder)storage.Children["C"]).Children["Pump"],
            ((VirtualFolder)storage.Children["D"]).Children["Pump"]
        ];
        Assert.All(moved, subject =>
        {
            Assert.IsType<Samples.Motor>(subject);
            Assert.NotSame(first, subject);
            Assert.NotSame(second, subject);
        });
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
    public async Task WhenJsonSubjectFileIsPutBackAfterBeingInvalid_ThenConfigurationIsNotAppliedAgain()
    {
        // Arrange
        var content = CallbackSubject.Serialize("first");
        WriteFile("Valve.json", content);
        var storage = await ConnectAsync();
        var valve = Assert.IsType<CallbackSubject>(storage.Children["Valve"]);
        WriteFile("Valve.json", "{");
        await storage.ReconcileAsync();
        var applyCount = 0;
        CallbackSubject.RunOnNextApply((_, _) =>
        {
            applyCount++;
            return Task.CompletedTask;
        });

        // Act
        WriteFile("Valve.json", content);
        await storage.ReconcileAsync();

        // Assert
        Assert.Same(valve, storage.Children["Valve"]);
        Assert.Equal("first", valve.Name);
        Assert.Equal(0, applyCount);
    }

    [Fact]
    public async Task WhenJsonSubjectFileIsPutBackAfterFailedApply_ThenSubjectHoldsThePreviousConfigurationAndSaveKeepsIt()
    {
        // Arrange
        WriteFile("Valve.json", CallbackSubject.Serialize("first"));
        var storage = await ConnectAsync();
        var valve = Assert.IsType<CallbackSubject>(storage.Children["Valve"]);
        CallbackSubject.RunOnNextApply((_, _) => throw new InvalidOperationException("The endpoint is unreachable."));
        WriteFile("Valve.json", CallbackSubject.Serialize("second, rejected"));
        await storage.ReconcileAsync(Named("Valve.json"));
        var nameAfterFailedApply = valve.Name;

        // Act
        WriteFile("Valve.json", CallbackSubject.Serialize("first"));
        await storage.ReconcileAsync(Named("Valve.json"));
        var nameAfterFileWasPutBack = valve.Name;
        await storage.WriteConfigurationAsync(valve, CancellationToken.None);

        // Assert
        Assert.Equal("second, rejected", nameAfterFailedApply);
        Assert.Equal("first", nameAfterFileWasPutBack);
        Assert.DoesNotContain("second, rejected", File.ReadAllText(GetFullPath("Valve.json")));
    }

    [Fact]
    public async Task WhenMarkdownFileIsPutBackAfterFailedLoad_ThenPageHoldsThePreviousContentAndSaveKeepsIt()
    {
        // Arrange
        var content = $"# Page\n\n```subject(valve)\n{CallbackSubject.Serialize("first")}\n```\n";
        var rejectedContent = content + "\n```subject(broken)\n{ \"$type\": \"typo, }\n```\n";
        WriteFile("Page.md", content);
        var storage = await ConnectAsync();
        var page = Assert.IsType<MarkdownFile>(storage.Children["Page.md"]);
        var valve = Assert.IsType<CallbackSubject>(page.Children["valve"]);
        WriteFile("Page.md", rejectedContent);
        await storage.ReconcileAsync(Named("Page.md"));
        var contentAfterFailedLoad = page.Content;

        // Act
        WriteFile("Page.md", content);
        await storage.ReconcileAsync(Named("Page.md"));
        var contentAfterFileWasPutBack = page.Content;
        valve.Name = "saved in the UI";
        await page.WriteConfigurationAsync(valve, CancellationToken.None);

        // Assert
        Assert.Equal(rejectedContent, contentAfterFailedLoad);
        Assert.Equal(content, contentAfterFileWasPutBack);
        Assert.DoesNotContain("subject(broken)", File.ReadAllText(GetFullPath("Page.md")));
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
        Assert.Equal(StorageStatus.Disconnected, statusAfterCancelledReconnect);
        Assert.True(laterReconnectWasWaiting);
        Assert.Equal(StorageStatus.Connected, storage.Status);
        Assert.Equal(["Notes.md", "Slow.gated"], storage.Children.Keys.Order());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenStorageConnectsAfterDispose_ThenConnectFailsAndNothingKeepsRunning(bool connectsByApplyingConfiguration)
    {
        // Arrange
        var timeProvider = new ManualTimeProvider();
        WriteFile("Notes.md");
        var storage = await ConnectAsync(configure: container =>
        {
            container.TimeProvider = timeProvider;
            container.ReconcileIntervalSeconds = 60;
        });
        storage.Dispose();
        WriteFile("Added.md");

        // Act
        var exception = await Record.ExceptionAsync(() => connectsByApplyingConfiguration
            ? storage.ApplyConfigurationAsync(CancellationToken.None)
            : storage.ConnectAsync(CancellationToken.None));

        // Assert
        Assert.IsType<ObjectDisposedException>(exception);
        Assert.Equal(StorageStatus.Disconnected, storage.Status);
        Assert.Equal(["Notes.md"], storage.Children.Keys);
        Assert.Equal(0, timeProvider.ArmedTimerCount);
    }

    [Fact]
    public async Task WhenStorageIsDisposedWhileReconnectStartsToFollowItsStorage_ThenNoTimerStaysArmed()
    {
        // Arrange
        var timeProvider = new ManualTimeProvider();
        var hookedTimeProvider = new HookedTimeProvider(timeProvider);
        var storage = await ConnectAsync(configure: container =>
        {
            container.TimeProvider = hookedTimeProvider;
            container.ReconcileIntervalSeconds = 60;
        });

        // The reconnect is held where it starts the trigger of the connection that it has just made the
        // current one. A dispose can end that connection only together with its trigger, so it waits.
        var dispose = new Thread(storage.Dispose);
        hookedTimeProvider.BeforeNextTimerIsCreated(() =>
        {
            dispose.Start();
            SpinWait.SpinUntil(() => dispose.ThreadState.HasFlag(ThreadState.WaitSleepJoin) || !dispose.IsAlive, DeadlockTimeout);
        });

        // Act
        await Record.ExceptionAsync(() => storage.ConnectAsync(CancellationToken.None));
        var hasDisposeEnded = dispose.Join(DeadlockTimeout);

        // Assert
        Assert.True(hasDisposeEnded);
        Assert.Equal(0, timeProvider.ArmedTimerCount);
        Assert.Equal(StorageStatus.Disconnected, storage.Status);
    }

    [Fact]
    public async Task WhenPassQueuedBehindFirstPassFails_ThenConnectDoesNotReplaceItsStatus()
    {
        // Arrange: what the connect does after its first pass is posted here and runs only when the test says so.
        PausableBlobStorage? client = null;
        FluentStorageContainer? storage = null;
        WriteFile("Slow.gated");
        var gate = GatedFile.PauseNextLoad();
        var connectContext = new ManualSynchronizationContext();
        var previousContext = SynchronizationContext.Current;
        Task<FluentStorageContainer> connect;
        SynchronizationContext.SetSynchronizationContext(connectContext);
        try
        {
            connect = ConnectAsync(configure: container =>
            {
                storage = container;
                container.ClientDecorator = inner => client = new PausableBlobStorage(inner);
            });
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        // A second pass is queued behind the first one, as the trigger does for a change during startup.
        await gate.WhenReachedAsync();
        var listingOfSecondPassReached = client!.PauseNext(nameof(IBlobStorage.ListAsync));
        var secondPass = storage!.ReconcileAsync();

        // Act
        gate.Release();
        await listingOfSecondPassReached;
        client.FailHangingCall(new IOException("The share is gone."));
        await secondPass;
        var statusAfterFailedPass = storage.Status;
        connectContext.RunAll();
        await connect;

        // Assert
        Assert.Equal(StorageStatus.Error, statusAfterFailedPass);
        Assert.Equal(StorageStatus.Error, storage.Status);
    }

    [Fact]
    public async Task WhenPassQueuedBehindFailedFirstPassFinishes_ThenConnectDoesNotReplaceItsStatus()
    {
        // Arrange: what the connect does after its first pass is posted here and runs only when the test says so.
        PausableBlobStorage? client = null;
        FluentStorageContainer? storage = null;
        Task? listingOfFirstPassReached = null;
        WriteFile("Notes.md");
        var connectContext = new ManualSynchronizationContext();
        var previousContext = SynchronizationContext.Current;
        Task<FluentStorageContainer> connect;
        SynchronizationContext.SetSynchronizationContext(connectContext);
        try
        {
            connect = ConnectAsync(configure: container =>
            {
                storage = container;
                container.ClientDecorator = inner =>
                {
                    client = new PausableBlobStorage(inner);
                    listingOfFirstPassReached = client.PauseNext(nameof(IBlobStorage.ListAsync));
                    return client;
                };
            });
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        // A second pass is queued behind the first one, as the trigger does for a change during startup.
        await listingOfFirstPassReached!;
        var secondPass = storage!.ReconcileAsync();

        // Act
        client!.FailHangingCall(new IOException("The share did not answer."));
        await secondPass;
        var statusAfterSecondPass = storage.Status;
        connectContext.RunAll();
        var exception = await Record.ExceptionAsync(() => connect);

        // Assert
        Assert.IsType<IOException>(exception);
        Assert.Equal(StorageStatus.Connected, statusAfterSecondPass);
        Assert.Equal(StorageStatus.Connected, storage.Status);
        Assert.Equal(["Notes.md"], storage.Children.Keys);
    }

    [Fact]
    public async Task WhenUnchangedConfigurationIsAppliedWhileStorageConnects_ThenItWaitsAndNothingIsLoadedAgain()
    {
        // Arrange
        WriteFile("Motor.json", SerializeMotor("Pump"));
        WriteFile("Slow.gated");
        var gate = GatedFile.PauseNextLoad();
        var storage = CreateStorage();
        var connect = storage.ConnectAsync(CancellationToken.None);
        await gate.WhenReachedAsync();

        // Act
        var apply = storage.ApplyConfigurationAsync(CancellationToken.None);
        var applyWasWaiting = !apply.IsCompleted;
        gate.Release();
        await connect;
        var motor = storage.Children["Motor"];
        await apply.WaitAsync(DeadlockTimeout);

        // Assert
        Assert.True(applyWasWaiting);
        Assert.Equal(StorageStatus.Connected, storage.Status);
        Assert.Same(motor, storage.Children["Motor"]);
        Assert.Equal(1, GatedFile.LoadCount);
    }

    [Fact]
    public async Task WhenOperationIsQueuedBehindPassThatReconnectCancels_ThenItFailsAsNotConnectedAndWritesNothing()
    {
        // Arrange
        WriteFile("Data.gated", "first");
        PausableBlobStorage? client = null;
        var hookedTimeProvider = new HookedTimeProvider(new ManualTimeProvider());
        var storage = await ConnectAsync(configure: container =>
        {
            container.TimeProvider = hookedTimeProvider;
            container.ReconcileIntervalSeconds = 60;
            container.ClientDecorator = inner => client = new PausableBlobStorage(inner);
        });
        var listingReached = client!.PauseNext(nameof(IBlobStorage.ListAsync));
        var pass = storage.ReconcileAsync();
        await listingReached;
        var write = WriteBlobAsync(storage, "Data.gated", "second");

        // Act: the reconnect is held after it has cancelled the connection and before it has closed the worker.
        var triggerDisposeReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        hookedTimeProvider.BeforeNextTimerIsDisposed(() =>
        {
            triggerDisposeReached.SetResult();
            release.Wait(DeadlockTimeout);
        });
        var reconnect = Task.Run(() => storage.ConnectAsync(CancellationToken.None));
        await triggerDisposeReached.Task.WaitAsync(DeadlockTimeout);
        var exception = await Record.ExceptionAsync(() => write.WaitAsync(DeadlockTimeout));
        release.Set();
        await reconnect.WaitAsync(DeadlockTimeout);
        await pass.WaitAsync(DeadlockTimeout);

        // Assert
        Assert.Equal("Storage not connected", Assert.IsType<InvalidOperationException>(exception).Message);
        Assert.Equal("first", File.ReadAllText(GetFullPath("Data.gated")));
        Assert.Equal(StorageStatus.Connected, storage.Status);
    }

    [Fact]
    public async Task WhenSubjectIsAddedWhileReconnectStartsToFollowItsStorage_ThenItIsAddedAfterTheFirstPass()
    {
        // Arrange
        WriteFile("Old.json", SerializeMotor("Old"));
        var hookedTimeProvider = new HookedTimeProvider(new ManualTimeProvider());
        var storage = await ConnectAsync(configure: container =>
        {
            container.TimeProvider = hookedTimeProvider;
            container.ReconcileIntervalSeconds = 60;
        });
        var assignments = new ChildrenAssignmentRecorder(storage);
        Context!.AddService<IWriteInterceptor>(assignments);

        // Act: the reconnect is held where it starts the trigger, when its connection is already the current one.
        var triggerStartReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        hookedTimeProvider.BeforeNextTimerIsCreated(() =>
        {
            triggerStartReached.SetResult();
            release.Wait(DeadlockTimeout);
        });
        var reconnect = Task.Run(() => storage.ConnectAsync(CancellationToken.None));
        await triggerStartReached.Task.WaitAsync(DeadlockTimeout);
        var add = storage.AddSubjectAsync("New.json", CreateMotor("New"), CancellationToken.None);
        release.Set();
        await reconnect.WaitAsync(DeadlockTimeout);
        await add.WaitAsync(DeadlockTimeout);

        // Assert: an add that ran before the first pass would have assigned a tree that holds nothing else.
        Assert.Equal([["Old"], ["New", "Old"]], assignments.AssignedKeys);
    }

    [Fact]
    public async Task WhenStorageIsStoppedDuringItsFirstPass_ThenStatusIsDisconnected()
    {
        // Arrange
        var storage = CreateStorage();
        WriteFile("First.gated");
        WriteFile("Second.gated");
        var gate = GatedFile.PauseNextLoad();
        await storage.StartAsync(CancellationToken.None);
        await gate.WhenReachedAsync();

        // Act
        var stop = storage.StopAsync(CancellationToken.None);
        gate.Release();
        await stop;
        var exception = await Record.ExceptionAsync(() => storage.ExecuteTask!);

        // Assert
        Assert.IsType<OperationCanceledException>(exception, exactMatch: false);
        Assert.Equal(StorageStatus.Disconnected, storage.Status);
        Assert.Empty(storage.Children);
    }

    [Fact]
    public async Task WhenConnectIsCancelledByItsCallerDuringFirstPass_ThenLaterPassLoadsTheStorageAndSetsTheStatus()
    {
        // Arrange: without a periodic pass, so that only the retry of the cancelled pass can run one.
        var timeProvider = new ManualTimeProvider();
        var storage = CreateStorage(configure: container =>
        {
            container.TimeProvider = timeProvider;
            container.ReconcileIntervalSeconds = 0;
        });
        WriteFile("First.gated");
        WriteFile("Second.gated");
        var gate = GatedFile.PauseNextLoad();
        using var cancellation = new CancellationTokenSource();
        var connect = storage.ConnectAsync(cancellation.Token);
        await gate.WhenReachedAsync();
        await cancellation.CancelAsync();
        gate.Release();
        var exception = await Record.ExceptionAsync(() => connect);
        var statusAfterConnect = storage.Status;
        var childrenAfterConnect = storage.Children.Keys.ToList();

        // Act
        timeProvider.Advance(ReconcileTrigger.MaximumDelay);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => storage.Status == StorageStatus.Connected);
        Assert.IsType<OperationCanceledException>(exception, exactMatch: false);
        Assert.Equal(StorageStatus.Disconnected, statusAfterConnect);
        Assert.Empty(childrenAfterConnect);
        Assert.Equal(["First.gated", "Second.gated"], storage.Children.Keys.Order());
    }

    [Fact]
    public async Task WhenOperationsWaitBehindPassOfConnectionThatEnds_ThenTheyFailAsNotConnected()
    {
        // Arrange
        WriteFile("Motor.json", SerializeMotor("Pump"));
        var storage = await ConnectAsync();
        var motor = storage.Children["Motor"];
        WriteFile("Slow.gated");
        var gate = GatedFile.PauseNextLoad();
        var pass = storage.ReconcileAsync();
        await gate.WhenReachedAsync();
        using var content = new MemoryStream([1, 2, 3]);
        Task[] queuedOperations =
        [
            storage.AddSubjectAsync("New.json", CreateMotor("New"), CancellationToken.None),
            storage.WriteBlobAsync("Notes.txt", content, CancellationToken.None),
            storage.DeleteBlobAsync("Motor.json", CancellationToken.None),
            storage.DeleteSubjectAsync(motor, CancellationToken.None)
        ];
        var queuedSave = storage.WriteConfigurationAsync(motor, CancellationToken.None);

        // Act: the worker reaches the operations once the pass that the reconnect has cancelled gives way.
        var reconnect = storage.ConnectAsync(CancellationToken.None);
        var laterException = await Record.ExceptionAsync(() => storage.DeleteBlobAsync("Motor.json", CancellationToken.None));
        gate.Release();
        await pass;
        var exceptions = new List<Exception?>();
        foreach (var operation in queuedOperations)
        {
            exceptions.Add(await Record.ExceptionAsync(() => operation.WaitAsync(DeadlockTimeout)));
        }

        var isSaved = await queuedSave.WaitAsync(DeadlockTimeout);
        await reconnect;

        // Assert
        Assert.All(exceptions, exception =>
            Assert.Equal("Storage not connected", Assert.IsType<InvalidOperationException>(exception).Message));
        Assert.False(isSaved);
        Assert.Equal("Storage not connected", Assert.IsType<InvalidOperationException>(laterException).Message);
        Assert.Equal(["Motor", "Slow.gated"], storage.Children.Keys.Order());
        Assert.False(File.Exists(GetFullPath("Notes.txt")));
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
    public async Task WhenAssignmentToTreeFails_ThenPassFailsAndNextPassAssignsAgain()
    {
        // Arrange
        var storage = await ConnectAsync();
        var failingWrite = new FailingWrite(nameof(FluentStorageContainer.Children));
        Context!.AddService<IWriteInterceptor>(failingWrite);
        failingWrite.FailNext();
        WriteFile("Notes.md");

        // Act
        await storage.ReconcileAsync();
        var statusAfterFailedPass = storage.Status;
        var childrenAfterFailedPass = storage.Children.Keys.ToList();
        await storage.ReconcileAsync();

        // Assert
        Assert.Equal(StorageStatus.Error, statusAfterFailedPass);
        Assert.Empty(childrenAfterFailedPass);
        Assert.Equal(StorageStatus.Connected, storage.Status);
        Assert.Equal(["Notes.md"], storage.Children.Keys);
    }

    [Fact]
    public async Task WhenOneFolderChanges_ThenOnlyThatFolderIsAssigned()
    {
        // Arrange
        WriteFile("A/First.md");
        WriteFile("B/Second.md");
        var storage = await ConnectAsync();
        var changedFolder = (VirtualFolder)storage.Children["A"];
        var otherFolder = (VirtualFolder)storage.Children["B"];
        var assignmentsOfRoot = new ChildrenAssignmentRecorder(storage);
        var assignmentsOfChangedFolder = new ChildrenAssignmentRecorder(changedFolder);
        var assignmentsOfOtherFolder = new ChildrenAssignmentRecorder(otherFolder);
        Context!.AddService<IWriteInterceptor>(assignmentsOfRoot);
        Context.AddService<IWriteInterceptor>(assignmentsOfChangedFolder);
        Context.AddService<IWriteInterceptor>(assignmentsOfOtherFolder);
        WriteFile("A/Added.md");

        // Act
        await storage.ReconcileAsync();
        await storage.ReconcileAsync();

        // Assert
        Assert.Equal([["Added.md", "First.md"]], assignmentsOfChangedFolder.AssignedKeys);
        Assert.Empty(assignmentsOfOtherFolder.AssignedKeys);
        Assert.Empty(assignmentsOfRoot.AssignedKeys);
    }

    [Fact]
    public async Task WhenConnectIsCancelledWhileItsLastFileLoads_ThenNextPassLoadsThatFile()
    {
        // Arrange: the load ends with the cancellation of the connect, which is no failure of the file.
        var storage = await ConnectAsync();
        WriteFile("Only.gated");
        var loadReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        GatedFile.RunOnNextLoad(async (_, cancellationToken) =>
        {
            loadReached.SetResult();
            await new TaskCompletionSource().Task.WaitAsync(cancellationToken);
        });
        using var cancellation = new CancellationTokenSource();
        var reconnect = storage.ConnectAsync(cancellation.Token);
        await loadReached.Task.WaitAsync(DeadlockTimeout);

        // Act
        await cancellation.CancelAsync();
        var exception = await Record.ExceptionAsync(() => reconnect);
        var childrenAfterCancelledConnect = storage.Children.Keys.ToList();
        await storage.ReconcileAsync();

        // Assert
        Assert.IsType<OperationCanceledException>(exception, exactMatch: false);
        Assert.Empty(childrenAfterCancelledConnect);
        Assert.Equal(["Only.gated"], storage.Children.Keys);
        Assert.Equal(StorageStatus.Connected, storage.Status);
    }

    [Fact]
    public async Task WhenRefreshAfterBlobWriteFailsAndFileKeepsSizeAndTime_ThenNextPassStillReloadsIt()
    {
        // Arrange
        WriteFile("Data.gated", "aaaa");
        var storage = await ConnectAsync();
        var file = (GatedFile)storage.Children["Data.gated"];
        var modified = File.GetLastWriteTimeUtc(GetFullPath("Data.gated"));
        GatedFile.FailNextLoad();

        // Act: the write leaves the file with the size and, set back here, the time that are recorded for it.
        await WriteBlobAsync(storage, "Data.gated", "bbbb");
        File.SetLastWriteTimeUtc(GetFullPath("Data.gated"), modified);
        var contentAfterWrite = file.Content;
        await storage.ReconcileAsync();

        // Assert
        Assert.Equal("aaaa", contentAfterWrite);
        Assert.Equal("bbbb", file.Content);
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
    public async Task WhenMarkdownFileLoadsEmbeddedSubjects_ThenLifecycleHandlersRunWithoutTheFlowOfThePass()
    {
        // Arrange
        var firstBlock = $"```subject(first)\n{CallbackSubject.Serialize("first")}\n```\n";
        var secondBlock = $"```subject(second)\n{CallbackSubject.Serialize("second")}\n```\n";
        var storage = await ConnectAsync();
        var recorder = new LifecycleFlowRecorder();
        Context!.AddService<ILifecycleHandler>(recorder);

        // Act: the first pass creates the page with one embedded subject. The second reloads the page that is
        // in the tree by then, which updates that subject, creates another one and assigns both to the page.
        WriteFile("Page.md", $"# Page\n\n{firstBlock}");
        await storage.ReconcileAsync();
        WriteFile("Page.md", $"# Page, changed\n\n{firstBlock}\n{secondBlock}");
        await storage.ReconcileAsync();

        // Assert
        var page = Assert.IsType<MarkdownFile>(storage.Children["Page.md"]);
        var changes = recorder.Changes;
        Assert.Equal(2, changes.Count(change => change is { Subject: CallbackSubject, IsContextAttach: true }));
        Assert.Contains(changes, change => ReferenceEquals(change.Subject, page.Children["second"]) && !change.IsContextAttach);
        Assert.Contains(changes, change => change.Subject is HtmlSegment);
        Assert.All(changes, change => Assert.True(change.IsFlowSuppressed, $"{change.Subject.GetType().Name}, context attach: {change.IsContextAttach}"));
    }

    [Fact]
    public async Task WhenMarkdownFileUpdatesEmbeddedSubject_ThenItsConfigurationIsWrittenWithoutTheFlowOfThePass()
    {
        // Arrange
        WriteFile("Page.md", $"# Page\n\n```subject(embedded)\n{CallbackSubject.Serialize("first")}\n```\n");
        var storage = await ConnectAsync();
        var embedded = Assert.IsType<CallbackSubject>(Assert.IsType<MarkdownFile>(storage.Children["Page.md"]).Children["embedded"]);
        var recorder = new ConfigurationWriteFlowRecorder(embedded);
        Context!.AddService<IWriteInterceptor>(recorder);

        // Act
        WriteFile("Page.md", $"# Page\n\n```subject(embedded)\n{CallbackSubject.Serialize("second, with another size")}\n```\n");
        await storage.ReconcileAsync();

        // Assert
        Assert.Equal("second, with another size", embedded.Name);
        Assert.Equal([true], recorder.IsFlowSuppressedPerWrite);
    }

    [Fact]
    public async Task WhenChangedJsonSubjectFileIsApplied_ThenItsConfigurationIsWrittenWithoutTheFlowOfThePass()
    {
        // Arrange
        WriteFile("Subject.json", CallbackSubject.Serialize("first"));
        var storage = await ConnectAsync();
        var subject = Assert.IsType<CallbackSubject>(storage.Children["Subject"]);
        var recorder = new ConfigurationWriteFlowRecorder(subject);
        Context!.AddService<IWriteInterceptor>(recorder);

        // Act
        WriteFile("Subject.json", CallbackSubject.Serialize("second, with another size"));
        await storage.ReconcileAsync();

        // Assert
        Assert.Equal("second, with another size", subject.Name);
        Assert.Equal([true], recorder.IsFlowSuppressedPerWrite);
    }

    [Fact]
    public async Task WhenPassReleasesSubjectThatIsNotPlaced_ThenLifecycleHandlersRunWithoutTheFlowOfThePass()
    {
        // Arrange: the folder "Motor" holds the key that Motor.json asks for.
        WriteFile("Motor/Page.md");
        var storage = await ConnectAsync();
        var recorder = new LifecycleFlowRecorder();
        Context!.AddService<ILifecycleHandler>(recorder);
        WriteFile("Motor.json", SerializeMotor("Pump"));

        // Act
        await storage.ReconcileAsync();

        // Assert
        var changes = recorder.Changes;
        Assert.IsType<VirtualFolder>(storage.Children["Motor"]);
        Assert.Contains(changes, change => change is { Subject: Samples.Motor, IsContextDetach: true });
        Assert.All(changes, change => Assert.True(change.IsFlowSuppressed, $"{change.Subject.GetType().Name}, context attach: {change.IsContextAttach}"));
    }

    [Fact]
    public async Task WhenPassCreatesFileSubject_ThenItsConstructorRunsWithoutTheFlowOfThePass()
    {
        // Arrange
        var storage = await ConnectAsync();
        WriteFile("Data.gated");

        // Act
        await storage.ReconcileAsync();

        // Assert
        Assert.IsType<GatedFile>(storage.Children["Data.gated"]);
        Assert.Equal(0, GatedFile.CreatedWithFlowCount);
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

    /// <summary>
    /// Keeps what is posted to it until the test runs it, to hold the code after an await.
    /// </summary>
    private sealed class ManualSynchronizationContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _posted = new();

        public override void Post(SendOrPostCallback callback, object? state) => _posted.Enqueue((callback, state));

        public void RunAll()
        {
            while (_posted.TryDequeue(out var posted))
            {
                posted.Callback(posted.State);
            }
        }
    }

    private sealed class LifecycleFlowRecorder : ILifecycleHandler
    {
        private readonly List<(IInterceptorSubject Subject, bool IsContextAttach, bool IsContextDetach, bool IsFlowSuppressed)> _changes = [];

        public IReadOnlyList<(IInterceptorSubject Subject, bool IsContextAttach, bool IsContextDetach, bool IsFlowSuppressed)> Changes
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
            if (change.Subject is Samples.Motor or MarkdownFile or CallbackSubject or HtmlSegment)
            {
                lock (_changes)
                {
                    _changes.Add((change.Subject, change.IsContextAttach, change.IsContextDetach, ExecutionContext.IsFlowSuppressed()));
                }
            }
        }
    }

    /// <summary>
    /// Records for every write of the name of one subject whether it ran without the execution flow.
    /// </summary>
    private sealed class ConfigurationWriteFlowRecorder(CallbackSubject subject) : IWriteInterceptor
    {
        private readonly List<bool> _isFlowSuppressedPerWrite = [];

        public IReadOnlyList<bool> IsFlowSuppressedPerWrite
        {
            get
            {
                lock (_isFlowSuppressedPerWrite)
                {
                    return _isFlowSuppressedPerWrite.ToList();
                }
            }
        }

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            if (ReferenceEquals(context.Property.Subject, subject) && context.Property.Name == nameof(CallbackSubject.Name))
            {
                lock (_isFlowSuppressedPerWrite)
                {
                    _isFlowSuppressedPerWrite.Add(ExecutionContext.IsFlowSuppressed());
                }
            }

            next(ref context);
        }
    }

    /// <summary>
    /// Records the keys of every dictionary that is assigned to the children of one subject.
    /// </summary>
    private sealed class ChildrenAssignmentRecorder(IInterceptorSubject subject) : IWriteInterceptor
    {
        private readonly List<string[]> _assignedKeys = [];

        public IReadOnlyList<string[]> AssignedKeys
        {
            get
            {
                lock (_assignedKeys)
                {
                    return _assignedKeys.ToList();
                }
            }
        }

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            if (ReferenceEquals(context.Property.Subject, subject) && context.NewValue is Dictionary<string, IInterceptorSubject> children)
            {
                lock (_assignedKeys)
                {
                    _assignedKeys.Add(children.Keys.Order().ToArray());
                }
            }

            next(ref context);
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
