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
        await storage.SetMetadataAsync(docs, null, Values("""{ "title": "Documentation", "icon": "MenuBook" }"""), CancellationToken.None);

        // Assert
        Assert.Equal("Documentation", docs.Title);
        Assert.Equal("MenuBook", docs.IconName);
        Assert.Equal("MenuBook", ReadMetadataFile("Docs/.metadata.json").Own.GetString(MetadataKeys.Icon));
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
        await storage.SetMetadataAsync(docs, null, Values("""{ "icon": "MenuBook" }"""), CancellationToken.None);

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
        await storage.SetMetadataAsync(docs, null, Values("""{ "title": "Documentation" }"""), CancellationToken.None);

        // Assert
        Assert.Equal("Documentation", docs.Title);
        Assert.Equal("Docs", docs.GetNavigationTitle());
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
        await storage.SetMetadataAsync(motor, null, Values("""{ "navTitle": "Pump", "icon": "Water" }"""), CancellationToken.None);
        var written = motor.GetMetadata();
        await storage.ReconcileAsync(Named("Devices/Motor.metadata.json"));

        // Assert
        Assert.Equal("Pump", written.GetString(MetadataKeys.NavigationTitle));
        Assert.Equal("Water", written.GetString(MetadataKeys.Icon));
        Assert.Same(written, motor.GetMetadata());
    }

    [Fact]
    public async Task WhenSubjectMetadataIsSetForAPage_ThenItIsRefused()
    {
        // Arrange
        WriteFile("Readme.md");
        var storage = await ConnectAsync();

        // Act & Assert
        await Assert.ThrowsAsync<StorageRefusedException>(() =>
            storage.SetMetadataAsync(storage.Children["Readme.md"], null, Values("""{ "navTitle": "Home" }"""), CancellationToken.None));
    }

    [Fact]
    public async Task WhenEveryKeyIsRemoved_ThenTheFileIsDeleted()
    {
        // Arrange
        WriteFile("Docs/.metadata.json", """{ "title": "Documentation" }""");
        var storage = await ConnectAsync();
        var docs = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);

        // Act
        await storage.SetMetadataAsync(docs, null, Values("""{ "title": null }"""), CancellationToken.None);

        // Assert
        Assert.False(File.Exists(GetFullPath("Docs/.metadata.json")));
        Assert.Null(docs.GetMetadata());
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
            storage.SetMetadataAsync(docs, null, Values("""{ "title": "Documentation" }"""), CancellationToken.None));
        Assert.False(Directory.Exists(GetFullPath("Docs")));
    }

    [Theory]
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
            "set" => () => storage.SetMetadataAsync(docs, null, Values("""{ "title": "Documentation" }"""), CancellationToken.None),
            "order" => () => MetadataStoreExtensions.SetOrderAsync(docs, ["a.md"], CancellationToken.None),
            _ => () => storage.SetMetadataAsync(motor, null, Values("""{ "navTitle": "Pump" }"""), CancellationToken.None)
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
            storage.SetMetadataAsync(docs, null, Values("""{ "title": "Documentation" }"""), CancellationToken.None));

        // Assert
        Assert.Contains("could not be read", exception.Message);
        Assert.True(File.Exists(GetFullPath("Docs/.metadata.json/Notes.md")));
    }

    [Theory]
    [InlineData("Docs/Motor.Metadata.json", "Docs/Motor.metadata.json")]
    [InlineData("Docs/.Metadata.json", "Docs/.metadata.json")]
    [InlineData("Docs/motor.metadata.json", "Docs/Motor.metadata.json")]
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
            storage.SetMetadataAsync(owner, null, Values("""{ "icon": "Water" }"""), CancellationToken.None));

        // Assert
        Assert.Contains($"'{actualPath}'", exception.Message);
        Assert.Contains($"'{StoragePath.GetName(expectedPath)}'", exception.Message);
        Assert.False(File.Exists(GetFullPath(expectedPath)));
    }

    [Fact]
    public async Task WhenTwoSubjectsDifferOnlyInCase_ThenEachMetadataFileIsReadAndWritten()
    {
        // Arrange
        WriteFile("Docs/Motor.json", SerializeMotor("Motor 1"));
        WriteFile("Docs/motor.json", SerializeMotor("Motor 2"));
        WriteFile("Docs/Motor.metadata.json", """{ "navTitle": "Upper" }""");
        WriteFile("Docs/motor.metadata.json", """{ "navTitle": "Lower" }""");
        var storage = await ConnectAsync();
        var docs = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);
        var upper = docs.Children["Motor"];
        var lower = docs.Children["motor"];

        // Act
        var upperRead = upper.GetNavigationTitle();
        var lowerRead = lower.GetNavigationTitle();
        await storage.SetMetadataAsync(upper, null, Values("""{ "icon": "Water" }"""), CancellationToken.None);
        await storage.SetMetadataAsync(lower, null, Values("""{ "icon": "Air" }"""), CancellationToken.None);

        // Assert
        Assert.Equal("Upper", upperRead);
        Assert.Equal("Lower", lowerRead);
        Assert.Equal("{\n  \"navTitle\": \"Upper\",\n  \"icon\": \"Water\"\n}\n", ReadText("Docs/Motor.metadata.json"));
        Assert.Equal("{\n  \"navTitle\": \"Lower\",\n  \"icon\": \"Air\"\n}\n", ReadText("Docs/motor.metadata.json"));
    }

    [Fact]
    public async Task WhenTwoSubjectsDifferOnlyInExtensionCase_ThenEachMetadataFileIsReadAndWritten()
    {
        // Arrange
        WriteFile("Docs/Motor.JSON", SerializeMotor("Motor 1"));
        WriteFile("Docs/motor.JSON", SerializeMotor("Motor 2"));
        WriteFile("Docs/Motor.metadata.json", """{ "navTitle": "Upper" }""");
        WriteFile("Docs/motor.metadata.json", """{ "navTitle": "Lower" }""");
        var storage = await ConnectAsync();
        var docs = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);
        var upper = docs.Children["Motor"];
        var lower = docs.Children["motor"];

        // Act
        var upperRead = upper.GetNavigationTitle();
        var lowerRead = lower.GetNavigationTitle();
        await storage.SetMetadataAsync(upper, null, Values("""{ "icon": "Water" }"""), CancellationToken.None);
        await storage.SetMetadataAsync(lower, null, Values("""{ "icon": "Air" }"""), CancellationToken.None);

        // Assert
        Assert.Equal("Upper", upperRead);
        Assert.Equal("Lower", lowerRead);
        Assert.Equal("{\n  \"navTitle\": \"Upper\",\n  \"icon\": \"Water\"\n}\n", ReadText("Docs/Motor.metadata.json"));
        Assert.Equal("{\n  \"navTitle\": \"Lower\",\n  \"icon\": \"Air\"\n}\n", ReadText("Docs/motor.metadata.json"));
    }

    [Fact]
    public async Task WhenTwoFoldersDifferOnlyInCase_ThenEachMetadataFileIsWritten()
    {
        // Arrange
        WriteFile("Docs/a.md");
        WriteFile("docs/b.md");
        WriteFile("Docs/.metadata.json", """{ "title": "Upper" }""");
        WriteFile("docs/.metadata.json", """{ "title": "Lower" }""");
        var storage = await ConnectAsync();
        var upper = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);
        var lower = Assert.IsType<VirtualFolder>(storage.Children["docs"]);

        // Act
        await storage.SetMetadataAsync(upper, null, Values("""{ "icon": "Water" }"""), CancellationToken.None);
        await storage.SetMetadataAsync(lower, null, Values("""{ "icon": "Air" }"""), CancellationToken.None);

        // Assert
        Assert.Contains("\"Upper\"", ReadText("Docs/.metadata.json"));
        Assert.Contains("\"Water\"", ReadText("Docs/.metadata.json"));
        Assert.Contains("\"Lower\"", ReadText("docs/.metadata.json"));
        Assert.Contains("\"Air\"", ReadText("docs/.metadata.json"));
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
        await MetadataStoreExtensions.SetOrderAsync(docs, ["b.md", "d.md"], CancellationToken.None);

        // Assert
        string[] expectedOrder = ["b.md", "d.md", "c.md", "a.md"];
        Assert.Equal(expectedOrder, docs.ChildOrder.ToArray());
        Assert.Equal(expectedOrder, ChildOrdering.GetShownNames(docs));
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
        await MetadataStoreExtensions.SetOrderAsync(storage, ["c.md"], CancellationToken.None);

        // Assert
        string[] expectedOrder = ["c.md", "a.md", "b.md"];
        Assert.Equal(expectedOrder, storage.ChildOrder.ToArray());
        Assert.Equal(expectedOrder, ReadMetadataFile(".metadata.json").Own.GetStringArray(MetadataKeys.Order).ToArray());
    }

    [Fact]
    public async Task WhenTheOrderWasChangedByHandSinceThePass_ThenTheOtherChildrenFollowTheShownOrder()
    {
        // Arrange
        WriteFile("a.md");
        WriteFile("b.md");
        WriteFile("c.md");
        WriteFile(".metadata.json", """{ "order": ["a.md", "b.md", "c.md"] }""");
        var storage = await ConnectAsync();
        WriteFile(".metadata.json", """{ "order": ["c.md", "b.md", "a.md"] }""");

        // Act
        await MetadataStoreExtensions.SetOrderAsync(storage, ["b.md"], CancellationToken.None);

        // Assert
        Assert.Equal(["b.md", "a.md", "c.md"], storage.ChildOrder.ToArray());
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
            MetadataStoreExtensions.SetOrderAsync(docs, names, CancellationToken.None));

        // Assert
        Assert.Contains($"'09{new string('x', 62)}…'", exception.Message);
        Assert.DoesNotContain(new string('x', 63), exception.Message);
        Assert.DoesNotContain("'10x", exception.Message);
        Assert.Contains("'a.md'", exception.Message);
    }

    [Theory]
    [InlineData("""{ "order": ["a.md", "x.md"] }""", "Names that are not children of the folder: 'x.md'. Its children: 'a.md', 'b.md'.")]
    [InlineData("""{ "Order": ["a.md", "b.md", "a.md"] }""", "Names given more than once: 'a.md'.")]
    public async Task WhenOrderIsSetAsMetadataWithNamesThatAreNoChildrenOrRepeated_ThenItIsRefused(string values, string expectedMessage)
    {
        // Arrange
        WriteFile("Docs/a.md");
        WriteFile("Docs/b.md");
        var storage = await ConnectAsync();
        var docs = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);

        // Act
        var exception = await Assert.ThrowsAsync<StorageRefusedException>(() =>
            storage.SetMetadataAsync(docs, null, Values(values), CancellationToken.None));

        // Assert
        Assert.Equal(expectedMessage, exception.Message);
        Assert.False(File.Exists(GetFullPath("Docs/.metadata.json")));
        Assert.Empty(docs.ChildOrder);
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
        Assert.Null(motor.GetMetadata());
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
