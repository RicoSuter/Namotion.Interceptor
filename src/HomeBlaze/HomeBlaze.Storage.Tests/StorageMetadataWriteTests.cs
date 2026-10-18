using System.Text.Json;
using HomeBlaze.Components.Abstractions.Pages;
using HomeBlaze.Services;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Internal;
using Namotion.Interceptor;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Storage.Tests;

public class StorageMetadataWriteTests : StorageTestBase
{
    private static JsonElement Values(string json) => JsonElement.Parse(json);

    private string ReadText(string relativePath) => File.ReadAllText(GetFullPath(relativePath));

    [Fact]
    public async Task WhenFolderMetadataIsSet_ThenTheFolderAndItsFileHaveIt()
    {
        // Arrange
        WriteFile("Docs/Readme.md");
        var storage = await ConnectAsync();
        var docs = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);

        // Act
        await storage.SetMetadataAsync(docs, Values("""{ "title": "Documentation", "icon": "MenuBook" }"""), CancellationToken.None);

        // Assert
        Assert.Equal("Documentation", docs.Title);
        Assert.Equal("MenuBook", docs.IconName);
        Assert.Equal(new FolderMetadata { Title = "Documentation", Icon = "MenuBook" }, ReadMetadataFile("Docs/.metadata.json").FolderMetadata);
    }

    [Fact]
    public async Task WhenTheFileWasChangedByHandSinceThePass_ThenAWriteKeepsTheChange()
    {
        // Arrange
        WriteFile("Docs/a.md");
        WriteFile("Docs/.metadata.json", """{ "title": "Docs" }""");
        var storage = await ConnectAsync();
        var docs = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);
        WriteFile("Docs/.metadata.json", """{ "title": "Docs", "color": "red" }""");

        // Act
        await storage.SetMetadataAsync(docs, Values("""{ "icon": "MenuBook" }"""), CancellationToken.None);

        // Assert
        Assert.Equal("{\n  \"title\": \"Docs\",\n  \"color\": \"red\",\n  \"icon\": \"MenuBook\"\n}\n", ReadText("Docs/.metadata.json"));
    }

    [Fact]
    public async Task WhenTheFileIsNotInTheIndexYet_ThenAWriteKeepsWhatItHoldsAndTheFolderHasItAtOnce()
    {
        // Arrange
        WriteFile("Docs/a.md");
        var storage = await ConnectAsync();
        var docs = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);
        WriteFile("Docs/.metadata.json", """{ "navTitle": "Docs" }""");

        // Act
        await storage.SetMetadataAsync(docs, Values("""{ "title": "Documentation" }"""), CancellationToken.None);

        // Assert
        Assert.Equal(new FolderMetadata { Title = "Documentation", NavigationTitle = "Docs" }, docs.Metadata);
        Assert.Equal("{\n  \"navTitle\": \"Docs\",\n  \"title\": \"Documentation\"\n}\n", ReadText("Docs/.metadata.json"));
    }

    [Fact]
    public async Task WhenSubjectMetadataIsSet_ThenTheSubjectHasItAndItsOwnWriteDoesNotReloadIt()
    {
        // Arrange
        WriteFile("Devices/Motor.json", SerializeMotor("Motor 1"));
        var storage = await ConnectAsync();
        var motor = Assert.IsType<VirtualFolder>(storage.Children["Devices"]).Children["Motor"];

        // Act
        await storage.SetMetadataAsync(motor, Values("""{ "navTitle": "Pump", "icon": "Water" }"""), CancellationToken.None);
        var written = motor.TryGetSubjectMetadata();
        await storage.ReconcileAsync(Named("Devices/Motor.metadata.json"));

        // Assert
        Assert.Equal(new SubjectMetadata { NavigationTitle = "Pump", Icon = "Water" }, written);
        Assert.Same(written, motor.TryGetSubjectMetadata());
    }

    [Fact]
    public async Task WhenSubjectMetadataIsSetForAPage_ThenItIsRefused()
    {
        // Arrange
        WriteFile("Readme.md");
        var storage = await ConnectAsync();

        // Act & Assert
        await Assert.ThrowsAsync<StorageRefusedException>(() =>
            storage.SetMetadataAsync(storage.Children["Readme.md"], Values("""{ "navTitle": "Home" }"""), CancellationToken.None));
    }

    [Fact]
    public async Task WhenEveryKeyIsRemoved_ThenTheFileIsDeleted()
    {
        // Arrange
        WriteFile("Docs/.metadata.json", """{ "title": "Documentation" }""");
        var storage = await ConnectAsync();
        var docs = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);

        // Act
        await storage.SetMetadataAsync(docs, Values("""{ "title": null }"""), CancellationToken.None);

        // Assert
        Assert.False(File.Exists(GetFullPath("Docs/.metadata.json")));
        Assert.Null(docs.Metadata);
        Assert.Equal("Docs", docs.Title);
    }

    [Fact]
    public async Task WhenFolderIsGoneFromTheStorage_ThenSettingItsMetadataFailsAndCreatesNoFolder()
    {
        // Arrange
        WriteFile("Docs/a.md");
        var storage = await ConnectAsync();
        var docs = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);
        Directory.Delete(GetFullPath("Docs"), recursive: true);
        await storage.ReconcileAsync();

        // Act & Assert
        await Assert.ThrowsAsync<StorageRefusedException>(() =>
            storage.SetMetadataAsync(docs, Values("""{ "title": "Documentation" }"""), CancellationToken.None));
        Assert.False(Directory.Exists(GetFullPath("Docs")));
    }

    [Theory]
    [InlineData("get")]
    [InlineData("set")]
    [InlineData("order")]
    [InlineData("subject")]
    public async Task WhenFolderIsGoneFromTheStorageBeforeAPass_ThenMetadataCallsAreRefusedAndCreateNoFolder(string call)
    {
        // Arrange
        WriteFile("Docs/a.md");
        WriteFile("Docs/Motor.json", SerializeMotor("Motor 1"));
        var storage = await ConnectAsync();
        var docs = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);
        var motor = docs.Children["Motor"];
        Directory.Delete(GetFullPath("Docs"), recursive: true);
        Func<Task> act = call switch
        {
            "get" => () => storage.GetMetadataAsync(docs, CancellationToken.None),
            "set" => () => storage.SetMetadataAsync(docs, Values("""{ "title": "Documentation" }"""), CancellationToken.None),
            "order" => () => storage.SetChildOrderAsync(docs, ["a.md"], CancellationToken.None),
            _ => () => storage.SetMetadataAsync(motor, Values("""{ "navTitle": "Pump" }"""), CancellationToken.None)
        };

        // Act & Assert
        await Assert.ThrowsAsync<StorageRefusedException>(act);
        Assert.False(Directory.Exists(GetFullPath("Docs")));
    }

    [Fact]
    public async Task WhenADirectoryHasTheNameOfTheMetadataFileOfAFolder_ThenAWriteIsRefusedAsUnreadable()
    {
        // Arrange
        WriteFile("Docs/a.md");
        WriteFile("Docs/.metadata.json/Notes.md");
        var storage = await ConnectAsync();
        var docs = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);

        // Act
        var exception = await Assert.ThrowsAsync<StorageRefusedException>(() =>
            storage.SetMetadataAsync(docs, Values("""{ "title": "Documentation" }"""), CancellationToken.None));

        // Assert
        Assert.Contains("could not be read", exception.Message);
        Assert.True(File.Exists(GetFullPath("Docs/.metadata.json/Notes.md")));
    }

    [Theory]
    [InlineData("Docs/Motor.Metadata.json", "Docs/Motor.metadata.json")]
    [InlineData("Docs/.Metadata.json", "Docs/.metadata.json")]
    public async Task WhenTheMetadataFileIsNamedInAnotherCase_ThenAWriteIsRefusedNamingBothNames(string actualPath, string expectedPath)
    {
        // Arrange
        WriteFile("Docs/a.md");
        WriteFile("Docs/Motor.json", SerializeMotor("Motor 1"));
        WriteFile(actualPath, """{ "navTitle": "Pump" }""");
        var storage = await ConnectAsync();
        var docs = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);
        IInterceptorSubject owner = expectedPath.EndsWith("/.metadata.json") ? docs : docs.Children["Motor"];

        // Act
        var exception = await Assert.ThrowsAsync<StorageRefusedException>(() =>
            storage.SetMetadataAsync(owner, Values("""{ "icon": "Water" }"""), CancellationToken.None));

        // Assert
        Assert.Contains($"'{actualPath}'", exception.Message);
        Assert.Contains($"'{StoragePath.GetName(expectedPath)}'", exception.Message);
        Assert.False(File.Exists(GetFullPath(expectedPath)));
    }

    [Fact]
    public async Task WhenOrderIsSet_ThenTheNamesComeFirstTheOtherChildrenKeepTheirOrderAndOtherKeysStay()
    {
        // Arrange
        WriteFile("Docs/a.md");
        WriteFile("Docs/b.md");
        WriteFile("Docs/c.md");
        WriteFile("Docs/d.md");
        WriteFile("Docs/.metadata.json", """{ "order": ["d.md", "c.md", "b.md", "a.md"], "color": "red" }""");
        var storage = await ConnectAsync();
        var docs = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);

        // Act
        await storage.SetChildOrderAsync(docs, ["b.md", "d.md"], CancellationToken.None);

        // Assert
        string[] expectedOrder = ["b.md", "d.md", "c.md", "a.md"];
        Assert.Equal(expectedOrder, docs.ChildOrder.ToArray());
        Assert.Equal(expectedOrder, await storage.GetChildOrderAsync(docs, CancellationToken.None));
        Assert.Contains("\"color\": \"red\"", ReadText("Docs/.metadata.json"));
    }

    [Fact]
    public async Task WhenOrderOfTheRootIsSet_ThenTheMetadataFileOfTheRootHasIt()
    {
        // Arrange
        WriteFile("a.md");
        WriteFile("b.md");
        WriteFile("c.md");
        var storage = await ConnectAsync();

        // Act
        await storage.SetChildOrderAsync(storage, ["c.md"], CancellationToken.None);

        // Assert
        string[] expectedOrder = ["c.md", "a.md", "b.md"];
        Assert.Equal(expectedOrder, storage.ChildOrder.ToArray());
        Assert.Equal(expectedOrder, ReadMetadataFile(".metadata.json").ChildOrder.ToArray());
    }

    [Fact]
    public async Task WhenTheOrderWasChangedByHandSinceThePass_ThenTheOtherChildrenFollowTheChangedOrder()
    {
        // Arrange
        WriteFile("a.md");
        WriteFile("b.md");
        WriteFile("c.md");
        WriteFile(".metadata.json", """{ "order": ["a.md", "b.md", "c.md"] }""");
        var storage = await ConnectAsync();
        WriteFile(".metadata.json", """{ "order": ["c.md", "b.md", "a.md"] }""");

        // Act
        await storage.SetChildOrderAsync(storage, ["b.md"], CancellationToken.None);

        // Assert
        Assert.Equal(["b.md", "c.md", "a.md"], storage.ChildOrder.ToArray());
    }

    [Fact]
    public async Task WhenOrderHasAnUnknownName_ThenSettingItFailsNamingItAndWritesNothing()
    {
        // Arrange
        WriteFile("Docs/a.md");
        WriteFile("Docs/b.md");
        var storage = await ConnectAsync();
        var docs = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);

        // Act
        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            storage.SetChildOrderAsync(docs, ["b.md", "gone.md"], CancellationToken.None));

        // Assert
        Assert.Contains("'gone.md'", exception.Message);
        Assert.Contains("'a.md', 'b.md'", exception.Message);
        Assert.False(File.Exists(GetFullPath("Docs/.metadata.json")));
        Assert.Empty(docs.ChildOrder.ToArray());
    }

    [Fact]
    public async Task WhenOrderHasManyLongUnknownNames_ThenTheMessageShowsTheFirstOnesShortened()
    {
        // Arrange
        WriteFile("Docs/a.md");
        var storage = await ConnectAsync();
        var docs = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);
        var names = Enumerable.Range(0, 20).Select(index => $"{index:D2}{new string('x', 1000)}").ToArray();

        // Act
        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            storage.SetChildOrderAsync(docs, names, CancellationToken.None));

        // Assert
        Assert.Contains($"'09{new string('x', 62)}…'", exception.Message);
        Assert.DoesNotContain(new string('x', 63), exception.Message);
        Assert.DoesNotContain("'10x", exception.Message);
        Assert.Contains("'a.md'", exception.Message);
    }

    [Fact]
    public async Task WhenOrderHasANameTwice_ThenSettingItFailsNamingIt()
    {
        // Arrange
        WriteFile("Docs/a.md");
        WriteFile("Docs/b.md");
        var storage = await ConnectAsync();
        var docs = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);

        // Act
        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            storage.SetChildOrderAsync(docs, ["b.md", "a.md", "b.md"], CancellationToken.None));

        // Assert
        Assert.Contains("'b.md'", exception.Message);
        Assert.DoesNotContain("'a.md'", exception.Message);
        Assert.False(File.Exists(GetFullPath("Docs/.metadata.json")));
    }

    [Fact]
    public async Task WhenOrderIsRead_ThenTheChildrenAreInTheOrderInWhichTheyAreShown()
    {
        // Arrange
        WriteFile("Docs/a.md");
        WriteFile("Docs/b.md");
        WriteFile("Docs/c.md");
        WriteFile("Docs/.metadata.json", """{ "order": ["c.md", "gone.md"] }""");
        WriteFile("Readme.md");
        var storage = await ConnectAsync();
        var docs = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);

        // Act
        var folderOrder = await storage.GetChildOrderAsync(docs, CancellationToken.None);
        var rootOrder = await storage.GetChildOrderAsync(storage, CancellationToken.None);

        // Assert
        Assert.Equal(["c.md", "a.md", "b.md"], folderOrder);
        Assert.Equal(["Readme.md", "Docs"], rootOrder);
    }

    [Fact]
    public async Task WhenSubjectIsDeleted_ThenItsMetadataFileIsDeleted()
    {
        // Arrange
        WriteFile("Motor.json", SerializeMotor("Motor 1"));
        WriteFile("Motor.metadata.json", """{ "navTitle": "Pump" }""");
        var storage = await ConnectAsync();

        // Act
        await storage.DeleteSubjectAsync(storage.Children["Motor"], CancellationToken.None);

        // Assert
        Assert.False(File.Exists(GetFullPath("Motor.json")));
        Assert.False(File.Exists(GetFullPath("Motor.metadata.json")));
    }

    [Fact]
    public async Task WhenAPlainJsonFileIsDeleted_ThenItsMetadataFileIsDeleted()
    {
        // Arrange
        WriteFile("Notes.json", "{ not json");
        WriteFile("Notes.metadata.json", """{ "navTitle": "Notes" }""");
        var storage = await ConnectAsync();

        // Act
        await storage.DeleteSubjectAsync(Assert.IsType<JsonFile>(storage.Children["Notes.json"]), CancellationToken.None);

        // Assert
        Assert.False(File.Exists(GetFullPath("Notes.json")));
        Assert.False(File.Exists(GetFullPath("Notes.metadata.json")));
    }

    [Fact]
    public async Task WhenTheDeleteIsCanceledAfterTheFileIsGone_ThenTheMetadataFileIsDeletedAnyway()
    {
        // Arrange
        WriteFile("Motor.json", SerializeMotor("Motor 1"));
        WriteFile("Motor.metadata.json", """{ "navTitle": "Pump" }""");
        var storage = await ConnectAsync();
        var motor = storage.Children["Motor"];
        using var cancellation = new CancellationTokenSource();
        Context!.AddService<ILifecycleHandler>(new CancelOnDetach(motor, cancellation));

        // Act
        await storage.DeleteSubjectAsync(motor, cancellation.Token);

        // Assert
        Assert.True(cancellation.IsCancellationRequested);
        Assert.False(File.Exists(GetFullPath("Motor.json")));
        Assert.False(File.Exists(GetFullPath("Motor.metadata.json")));
    }

    [Fact]
    public async Task WhenSubjectIsDeletedAndAFolderHasTheNameOfItsMetadataFile_ThenTheFolderIsKept()
    {
        // Arrange
        WriteFile("Motor.json", SerializeMotor("Motor 1"));
        WriteFile("Motor.metadata.json/Notes.md");
        var storage = await ConnectAsync();

        // Act
        await storage.DeleteSubjectAsync(storage.Children["Motor"], CancellationToken.None);

        // Assert
        Assert.False(File.Exists(GetFullPath("Motor.json")));
        Assert.True(File.Exists(GetFullPath("Motor.metadata.json/Notes.md")));
    }

    [Theory]
    [InlineData("Motor.metadata.json")]
    [InlineData("Motor.Metadata.json")]
    public async Task WhenSubjectNameEndsWithMetadata_ThenAddingItFails(string path)
    {
        // Arrange
        var storage = await ConnectAsync();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            storage.AddSubjectAsync(path, CreateMotor(), CancellationToken.None));
    }

    [Fact]
    public async Task WhenTheDeleteFailsAfterTheFileIsGone_ThenTheMetadataFileIsDeletedAnyway()
    {
        // Arrange
        WriteFile("Motor.json", SerializeMotor("Motor 1"));
        WriteFile("Motor.metadata.json", """{ "navTitle": "Pump" }""");
        PausableBlobStorage? client = null;
        var storage = await ConnectAsync(configure: container => container.ClientDecorator = inner => client = new PausableBlobStorage(inner));
        client!.FailNextDeleteAfterItRan(new IOException("The answer was lost."));

        // Act
        await Assert.ThrowsAsync<IOException>(() => storage.DeleteSubjectAsync(storage.Children["Motor"], CancellationToken.None));

        // Assert
        Assert.False(File.Exists(GetFullPath("Motor.json")));
        Assert.False(File.Exists(GetFullPath("Motor.metadata.json")));
    }

    [Fact]
    public async Task WhenTheDeleteFailsAfterTheFileIsGone_ThenTheSubjectLosesItsMetadataAtOnce()
    {
        // Arrange
        WriteFile("Motor.json", SerializeMotor("Motor 1"));
        WriteFile("Motor.metadata.json", """{ "navTitle": "Pump" }""");
        PausableBlobStorage? client = null;
        var storage = await ConnectAsync(configure: container => container.ClientDecorator = inner => client = new PausableBlobStorage(inner));
        var motor = storage.Children["Motor"];
        client!.FailNextDeleteAfterItRan(new IOException("The answer was lost."));

        // Act
        await Assert.ThrowsAsync<IOException>(() => storage.DeleteSubjectAsync(motor, CancellationToken.None));

        // Assert
        Assert.Null(motor.TryGetSubjectMetadata());
    }

    [Fact]
    public async Task WhenTheDeleteFailsAndItIsUnknownWhetherTheFileIsGone_ThenTheWarningNamesTheFile()
    {
        // Arrange
        WriteFile("Motor.json", SerializeMotor("Motor 1"));
        WriteFile("Motor.metadata.json", """{ "navTitle": "Pump" }""");
        PausableBlobStorage? client = null;
        var logger = new RecordingLogger<FluentStorageContainer>();
        var storage = CreateStorage(configure: container => container.ClientDecorator = inner => client = new PausableBlobStorage(inner), logger: logger);
        await storage.ConnectAsync(CancellationToken.None);
        client!.FailNextDeleteAfterItRan(new IOException("The answer was lost."));
        var existsReached = client.PauseNext(nameof(PausableBlobStorage.ExistsAsync));

        // Act
        var delete = storage.DeleteSubjectAsync(storage.Children["Motor"], CancellationToken.None);
        await existsReached;
        client.FailHangingCall(new IOException("No answer."));
        await Assert.ThrowsAsync<IOException>(() => delete);

        // Assert
        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("Motor.json", warning);
        Assert.DoesNotContain("deleted subject", warning);
        Assert.True(File.Exists(GetFullPath("Motor.metadata.json")));
    }

    [Fact]
    public async Task WhenTheTreeFailsToFollowADelete_ThenTheMetadataFileIsDeletedAnyway()
    {
        // Arrange
        WriteFile("Motor.json", SerializeMotor("Motor 1"));
        WriteFile("Motor.metadata.json", """{ "navTitle": "Pump" }""");
        var storage = await ConnectAsync();
        var motor = storage.Children["Motor"];
        Context!.AddService<ILifecycleHandler>(new ThrowOnDetach(motor));

        // Act
        await Assert.ThrowsAsync<InvalidOperationException>(() => storage.DeleteSubjectAsync(motor, CancellationToken.None));

        // Assert
        Assert.False(File.Exists(GetFullPath("Motor.json")));
        Assert.False(File.Exists(GetFullPath("Motor.metadata.json")));
    }

    private sealed class ThrowOnDetach(IInterceptorSubject subject) : ILifecycleHandler
    {
        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            if (change.IsContextDetach && ReferenceEquals(change.Subject, subject))
            {
                throw new InvalidOperationException("The tree failed.");
            }
        }
    }

    // Cancels the delete of the subject once it has left the tree, which is after its file was deleted.
    private sealed class CancelOnDetach(IInterceptorSubject subject, CancellationTokenSource cancellation) : ILifecycleHandler
    {
        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            if (change.IsContextDetach && ReferenceEquals(change.Subject, subject))
            {
                cancellation.Cancel();
            }
        }
    }
}
