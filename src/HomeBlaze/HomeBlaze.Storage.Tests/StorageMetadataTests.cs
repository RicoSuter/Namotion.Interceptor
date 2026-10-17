using HomeBlaze.Components.Abstractions.Pages;
using HomeBlaze.Services;
using Namotion.Interceptor.Testing;

namespace HomeBlaze.Storage.Tests;

public class StorageMetadataTests : StorageTestBase
{
    [Fact]
    public async Task WhenMetadataFilesExist_ThenTheyDoNotBecomeSubjects()
    {
        // Arrange
        WriteFile(".metadata.json", """{ "order": ["Docs"] }""");
        WriteFile("Docs/.metadata.json", """{ "title": "Documentation" }""");
        WriteFile("Docs/Motor.json", SerializeMotor("Pump"));
        WriteFile("Docs/Motor.metadata.json", """{ "navTitle": "Pump" }""");
        WriteFile("Docs/Readme.md.metadata.json", """{ "navTitle": "Ignored" }""");

        // Act
        var storage = await ConnectAsync();

        // Assert
        Assert.Equal(["Docs"], storage.Children.Keys);
        var docs = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);
        Assert.Equal(["Motor"], docs.Children.Keys);
    }

    [Fact]
    public async Task WhenFolderHasMetadataFile_ThenFolderHasItsMetadataAndOrder()
    {
        // Arrange
        WriteFile("Docs/a.md");
        WriteFile("Docs/b.md");
        WriteFile("Docs/.metadata.json", """{ "title": "Documentation", "navTitle": "Docs", "icon": "MenuBook", "order": ["b.md", "a.md"] }""");

        // Act
        var storage = await ConnectAsync();

        // Assert
        var docs = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);
        Assert.Equal(new FolderMetadata("Documentation", "Docs", "MenuBook"), docs.Metadata);
        Assert.Equal("Documentation", docs.Title);
        Assert.Equal("MenuBook", docs.IconName);
        Assert.Equal(["b.md", "a.md"], docs.ChildOrder.ToArray());
    }

    [Fact]
    public async Task WhenFolderHasNoMetadataFile_ThenItsNameAndFolderIconApply()
    {
        // Arrange
        WriteFile("Docs/a.md");

        // Act
        var storage = await ConnectAsync();

        // Assert
        var docs = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);
        Assert.Null(docs.Metadata);
        Assert.Equal("Docs", docs.Title);
        Assert.Equal("Folder", docs.IconName);
        Assert.Empty(docs.ChildOrder);
    }

    [Fact]
    public async Task WhenRootHasMetadataFile_ThenRootHasItsOrderAndNoMetadata()
    {
        // Arrange
        WriteFile("a.md");
        WriteFile("b.md");
        WriteFile(".metadata.json", """{ "title": "Ignored", "order": ["b.md"] }""");

        // Act
        var storage = await ConnectAsync();

        // Assert
        Assert.Equal(["b.md"], storage.ChildOrder.ToArray());
        Assert.Null(storage.Metadata);
        Assert.False(storage.HasOwnMetadata);
    }

    [Fact]
    public async Task WhenFolderMetadataFileChanges_ThenFolderFollows()
    {
        // Arrange
        WriteFile("Docs/.metadata.json", """{ "title": "First" }""");
        var storage = await ConnectAsync();
        var docs = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);
        WriteFile("Docs/.metadata.json", """{ "title": "Second title" }""");

        // Act
        await storage.ReconcileAsync(Named("Docs/.metadata.json"));

        // Assert
        Assert.Same(docs, storage.Children["Docs"]);
        Assert.Equal("Second title", docs.Title);
    }

    [Fact]
    public async Task WhenFolderMetadataFileIsDeleted_ThenFolderHasNoMetadata()
    {
        // Arrange
        WriteFile("Docs/.metadata.json", """{ "title": "First", "order": ["a.md"] }""");
        var storage = await ConnectAsync();
        var docs = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);
        File.Delete(GetFullPath("Docs/.metadata.json"));

        // Act
        await storage.ReconcileAsync(Named("Docs/.metadata.json"));

        // Assert
        Assert.Null(docs.Metadata);
        Assert.Empty(docs.ChildOrder);
    }

    [Fact]
    public async Task WhenFolderMetadataFileIsNotValid_ThenFolderHasNoMetadataAndOtherFilesLoad()
    {
        // Arrange
        WriteFile("Docs/.metadata.json", "not json");
        WriteFile("Docs/a.md");

        // Act
        var storage = await ConnectAsync();

        // Assert
        var docs = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);
        Assert.Null(docs.Metadata);
        Assert.Equal(["a.md"], docs.Children.Keys);
    }

    [Fact]
    public async Task WhenSubjectHasMetadataFile_ThenSubjectHasItsMetadata()
    {
        // Arrange
        WriteFile("Motor.json", SerializeMotor("Motor 1"));
        WriteFile("Motor.metadata.json", """{ "navTitle": "Pump", "icon": "Water", "location": "AppBar", "alignment": "Right" }""");

        // Act
        var storage = await ConnectAsync();

        // Assert
        Assert.Equal(
            new SubjectMetadata("Pump", "Water", NavigationLocation.AppBar, AppBarAlignment.Right),
            storage.Children["Motor"].TryGetSubjectMetadata());
    }

    [Fact]
    public async Task WhenSubjectMetadataFileChangesAndIsDeleted_ThenSubjectFollows()
    {
        // Arrange
        WriteFile("Motor.json", SerializeMotor("Motor 1"));
        WriteFile("Motor.metadata.json", """{ "navTitle": "Pump" }""");
        var storage = await ConnectAsync();
        var motor = storage.Children["Motor"];

        // Act
        WriteFile("Motor.metadata.json", """{ "navTitle": "Main pump" }""");
        await storage.ReconcileAsync(Named("Motor.metadata.json"));
        var changed = motor.TryGetSubjectMetadata();
        File.Delete(GetFullPath("Motor.metadata.json"));
        await storage.ReconcileAsync(Named("Motor.metadata.json"));

        // Assert
        Assert.Equal("Main pump", changed?.NavigationTitle);
        Assert.Null(motor.TryGetSubjectMetadata());
    }

    [Fact]
    public async Task WhenSubjectIsReplacedByAnotherType_ThenTheNewSubjectHasTheMetadata()
    {
        // Arrange
        WriteFile("Motor.json", SerializeMotor("Motor 1"));
        WriteFile("Motor.metadata.json", """{ "navTitle": "Pump" }""");
        var storage = await ConnectAsync();
        WriteFile("Motor.json", SerializeStorage(CreateTemporaryDirectory().FullName));

        // Act
        await storage.ReconcileAsync(Named("Motor.json"));

        // Assert
        var replaced = Assert.IsType<FluentStorageContainer>(storage.Children["Motor"]);
        Assert.Equal("Pump", replaced.TryGetSubjectMetadata()?.NavigationTitle);
    }

    [Fact]
    public async Task WhenJsonSubjectMovesToAnotherFolder_ThenItTakesTheMetadataThereOrNone()
    {
        // Arrange
        WriteFile("A/Motor.json", SerializeMotor("Motor 1"));
        WriteFile("A/Motor.metadata.json", """{ "navTitle": "Pump" }""");
        WriteFile("B/x.md");
        var storage = await ConnectAsync();
        var motor = Assert.IsType<VirtualFolder>(storage.Children["A"]).Children["Motor"];
        Assert.Equal("Pump", motor.TryGetSubjectMetadata()?.NavigationTitle);

        // Act
        File.Move(GetFullPath("A/Motor.json"), GetFullPath("B/Motor.json"));
        await storage.ReconcileAsync(Named("A/Motor.json", "B/Motor.json"));
        var metadataAfterMove = motor.TryGetSubjectMetadata();
        WriteFile("B/Motor.metadata.json", """{ "navTitle": "Moved" }""");
        await storage.ReconcileAsync(Named("B/Motor.metadata.json"));

        // Assert
        var b = Assert.IsType<VirtualFolder>(storage.Children["B"]);
        Assert.Same(motor, b.Children["Motor"]);
        Assert.Null(metadataAfterMove);
        Assert.Equal("Moved", motor.TryGetSubjectMetadata()?.NavigationTitle);
    }

    [Fact]
    public async Task WhenFolderMetadataFileIsWritten_ThenWatcherAppliesIt()
    {
        // Arrange
        WriteFile("Docs/a.md");
        var storage = await ConnectAsync(enableFileWatching: true);
        var docs = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);

        // Act
        WriteFile("Docs/.metadata.json", """{ "title": "Documentation" }""");

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => docs.Title == "Documentation", WatcherTimeout);
    }
}
