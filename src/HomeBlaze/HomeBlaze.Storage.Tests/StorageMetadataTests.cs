using HomeBlaze.Components.Abstractions.Pages;
using HomeBlaze.Services;
using HomeBlaze.Storage.Abstractions;
using HomeBlaze.Storage.Files;
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
    public async Task WhenAFileNameEndsWithMetadataInAnotherCase_ThenItIsIgnoredWithAWarning()
    {
        // Arrange
        WriteFile("Motor.json", SerializeMotor("Motor 1"));
        WriteFile("Motor.Metadata.json", """{ "navTitle": "Pump" }""");
        var logger = new RecordingLogger<FluentStorageContainer>();
        var storage = CreateStorage(logger: logger);

        // Act
        await storage.ConnectAsync(CancellationToken.None);

        // Assert
        Assert.Equal(["Motor"], storage.Children.Keys);
        Assert.Null(storage.Children["Motor"].GetMetadata());
        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("Motor.Metadata.json", warning);
        Assert.Contains("lower case", warning);
    }

    [Fact]
    public async Task WhenTheMetadataFileOfAFolderIsNamedInAnotherCase_ThenItIsIgnoredWithAWarning()
    {
        // Arrange
        WriteFile("Docs/a.md");
        WriteFile("Docs/.Metadata.json", """{ "title": "Documentation" }""");
        var logger = new RecordingLogger<FluentStorageContainer>();
        var storage = CreateStorage(logger: logger);

        // Act
        await storage.ConnectAsync(CancellationToken.None);

        // Assert
        var docs = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);
        Assert.Equal(["a.md"], docs.Children.Keys);
        Assert.Null(docs.GetMetadata());
        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("Docs/.Metadata.json", warning);
    }

    [Fact]
    public async Task WhenAPassLooksAtEveryFileAndTheIgnoredMetadataFileIsUnchanged_ThenItWarnsNoMore()
    {
        // Arrange
        WriteFile("Motor.json", SerializeMotor("Motor 1"));
        WriteFile("Motor.Metadata.json", """{ "navTitle": "Pump" }""");
        var logger = new RecordingLogger<FluentStorageContainer>();
        var storage = CreateStorage(logger: logger);
        await storage.ConnectAsync(CancellationToken.None);

        // Act
        await storage.ReconcileAsync(allNamed: true);

        // Assert
        Assert.Single(logger.Warnings);
    }

    [Fact]
    public async Task WhenAMetadataFileHoldsASubject_ThenAWarningSaysSo()
    {
        // Arrange
        WriteFile("Motor.metadata.json", SerializeMotor("Motor 1"));
        var logger = new RecordingLogger<FluentStorageContainer>();
        var storage = CreateStorage(logger: logger);

        // Act
        await storage.ConnectAsync(CancellationToken.None);

        // Assert
        Assert.Empty(storage.Children);
        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("Motor.metadata.json", warning);
        Assert.Contains("looks like a subject file", warning);
    }

    [Fact]
    public async Task WhenAMetadataFileHasTwoEntriesForOneTarget_ThenAWarningSaysWhichOneApplies()
    {
        // Arrange
        WriteFile("Motor.json", SerializeMotor("Motor 1"));
        WriteFile("Motor.metadata.json", """{ "$subjects": { "Channels[0]": { "navTitle": "First" }, "Channels/0": { "navTitle": "Second" } } }""");
        var logger = new RecordingLogger<FluentStorageContainer>();
        var storage = CreateStorage(logger: logger);

        // Act
        await storage.ConnectAsync(CancellationToken.None);

        // Assert
        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("Motor.metadata.json", warning);
        Assert.Contains("Channels/0", warning);
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
        Assert.Equal("Docs", docs.GetMetadata().GetString(MetadataKeys.NavigationTitle));
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
        Assert.Null(docs.GetMetadata());
        Assert.Equal("Docs", docs.Title);
        Assert.Equal("Folder", docs.IconName);
        Assert.Empty(docs.ChildOrder);
    }

    [Fact]
    public async Task WhenRootHasMetadataFile_ThenRootHasItsMetadataAndOrder()
    {
        // Arrange
        WriteFile("a.md");
        WriteFile("b.md");
        WriteFile(".metadata.json", """{ "color": "red", "order": ["b.md"] }""");

        // Act
        var storage = await ConnectAsync();

        // Assert
        Assert.Equal(["b.md"], storage.ChildOrder.ToArray());
        Assert.Equal("red", storage.GetMetadata().GetString("color"));
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
        Assert.Null(docs.GetMetadata());
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
        Assert.Null(docs.GetMetadata());
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
        var motor = storage.Children["Motor"];
        Assert.Equal("Pump", motor.GetNavigationTitle());
        Assert.Equal("Water", motor.GetNavigationIconName());
        Assert.Equal(NavigationLocation.AppBar, motor.GetNavigationLocation());
        Assert.Equal(AppBarAlignment.Right, motor.GetAppBarAlignment());
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
        var changed = motor.GetMetadata();
        File.Delete(GetFullPath("Motor.metadata.json"));
        await storage.ReconcileAsync(Named("Motor.metadata.json"));

        // Assert
        Assert.Equal("Main pump", changed.GetString(MetadataKeys.NavigationTitle));
        Assert.Null(motor.GetMetadata());
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
        Assert.Equal("Pump", motor.GetMetadata().GetString(MetadataKeys.NavigationTitle));

        // Act
        File.Move(GetFullPath("A/Motor.json"), GetFullPath("B/Motor.json"));
        await storage.ReconcileAsync(Named("A/Motor.json", "B/Motor.json"));
        var metadataAfterMove = motor.GetMetadata();
        WriteFile("B/Motor.metadata.json", """{ "navTitle": "Moved" }""");
        await storage.ReconcileAsync(Named("B/Motor.metadata.json"));

        // Assert
        var b = Assert.IsType<VirtualFolder>(storage.Children["B"]);
        Assert.Same(motor, b.Children["Motor"]);
        Assert.Null(metadataAfterMove);
        Assert.Equal("Moved", motor.GetMetadata().GetString(MetadataKeys.NavigationTitle));
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

    [Fact]
    public async Task WhenAJsonFileDoesNotLoadAsASubject_ThenItsPlainFileHasTheMetadataBesideIt()
    {
        // Arrange
        WriteFile("Notes.json", "{ not json");
        WriteFile("Notes.metadata.json", """{ "navTitle": "Pump" }""");

        // Act
        var storage = await ConnectAsync();

        // Assert
        var file = Assert.IsType<JsonFile>(storage.Children["Notes.json"]);
        Assert.Equal("Pump", file.GetMetadata().GetString(MetadataKeys.NavigationTitle));
    }

    [Fact]
    public async Task WhenADirectoryHasTheNameOfTheMetadataFileOfItsFolder_ThenItStaysHidden()
    {
        // Arrange
        WriteFile("Docs/a.md");
        WriteFile("Docs/.metadata.json/Notes.md");

        // Act
        var storage = await ConnectAsync();

        // Assert
        var docs = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);
        Assert.Equal(["a.md"], docs.Children.Keys);
        Assert.Null(docs.GetMetadata());
    }
}
