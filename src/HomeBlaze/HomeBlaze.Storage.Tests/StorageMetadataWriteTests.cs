using System.Text;
using HomeBlaze.Components.Abstractions.Pages;
using HomeBlaze.Services;
using HomeBlaze.Storage.Internal;

namespace HomeBlaze.Storage.Tests;

public class StorageMetadataWriteTests : StorageTestBase
{
    private StorageMetadataDocument ReadMetadataFile(string relativePath)
        => StorageMetadataDocument.Parse(File.ReadAllBytes(GetFullPath(relativePath)));

    [Fact]
    public async Task WhenFolderMetadataIsWritten_ThenFileAndFolderHaveIt()
    {
        // Arrange
        WriteFile("Docs/Readme.md");
        var storage = await ConnectAsync();
        var docs = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);

        // Act
        await docs.WriteMetadataAsync(new FolderMetadata("Documentation", "Docs", "MenuBook"), CancellationToken.None);

        // Assert
        Assert.Equal("Documentation", docs.Title);
        Assert.Equal("MenuBook", docs.IconName);
        Assert.Equal("Documentation", ReadMetadataFile("Docs/.metadata.json").Title);
    }

    [Fact]
    public async Task WhenFolderMetadataIsWritten_ThenOrderAndUnknownKeysAreKept()
    {
        // Arrange
        WriteFile("Docs/a.md");
        WriteFile("Docs/.metadata.json", """{ "order": ["a.md"], "color": "red" }""");
        var storage = await ConnectAsync();
        var docs = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);

        // Act
        await docs.WriteMetadataAsync(new FolderMetadata("Documentation", null, null), CancellationToken.None);

        // Assert
        var document = ReadMetadataFile("Docs/.metadata.json");
        string?[] expectedOrder = ["a.md"];
        Assert.Equal(expectedOrder, document.Order);
        Assert.True(document.ExtensionData!.ContainsKey("color"));
        Assert.Equal(["a.md"], docs.ChildOrder.ToArray());
    }

    [Fact]
    public async Task WhenFolderMetadataIsRemovedAndNothingElseIsInTheFile_ThenFileIsDeleted()
    {
        // Arrange
        WriteFile("Docs/.metadata.json", """{ "title": "Documentation" }""");
        var storage = await ConnectAsync();
        var docs = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);

        // Act
        await docs.WriteMetadataAsync(null, CancellationToken.None);

        // Assert
        Assert.False(File.Exists(GetFullPath("Docs/.metadata.json")));
        Assert.Null(docs.Metadata);
        Assert.Equal("Docs", docs.Title);
    }

    [Fact]
    public async Task WhenFolderMetadataIsOnlyBlank_ThenNoFileIsWritten()
    {
        // Arrange
        WriteFile("Docs/a.md");
        var storage = await ConnectAsync();
        var docs = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);

        // Act
        await docs.WriteMetadataAsync(new FolderMetadata(" ", "", null), CancellationToken.None);

        // Assert
        Assert.False(File.Exists(GetFullPath("Docs/.metadata.json")));
        Assert.Null(docs.Metadata);
    }

    [Fact]
    public async Task WhenFolderMetadataFileCannotBeRead_ThenWritingMetadataFailsAndKeepsTheFile()
    {
        // Arrange
        const string content = """{"order":["a.md"],"color":"red",}x""";
        WriteFile("Docs/a.md");
        WriteFile("Docs/.metadata.json", content);
        var storage = await ConnectAsync();
        var docs = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            docs.WriteMetadataAsync(new FolderMetadata("Documentation", null, null), CancellationToken.None));
        Assert.Equal(Encoding.UTF8.GetBytes(content), File.ReadAllBytes(GetFullPath("Docs/.metadata.json")));
    }

    [Fact]
    public async Task WhenFolderMetadataFileCannotBeRead_ThenMoveFailsAndKeepsTheFile()
    {
        // Arrange
        const string content = """{"order":["a.md"],"color":"red",}x""";
        WriteFile("Docs/a.md");
        WriteFile("Docs/b.md");
        WriteFile("Docs/.metadata.json", content);
        var storage = await ConnectAsync();
        var docs = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            docs.MoveChildAsync("b.md", "a.md", CancellationToken.None));
        Assert.Equal(Encoding.UTF8.GetBytes(content), File.ReadAllBytes(GetFullPath("Docs/.metadata.json")));
    }

    [Fact]
    public async Task WhenFolderIsGoneFromTheStorage_ThenWritingItsMetadataFailsAndCreatesNoFolder()
    {
        // Arrange
        WriteFile("Docs/a.md");
        var storage = await ConnectAsync();
        var docs = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);
        Directory.Delete(GetFullPath("Docs"), recursive: true);
        await storage.ReconcileAsync();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            docs.WriteMetadataAsync(new FolderMetadata("Documentation", null, null), CancellationToken.None));
        Assert.False(Directory.Exists(GetFullPath("Docs")));
    }

    [Fact]
    public async Task WhenSubjectMetadataIsWritten_ThenSubjectHasItAndItsOwnWriteDoesNotReloadIt()
    {
        // Arrange
        WriteFile("Devices/Motor.json", SerializeMotor("Motor 1"));
        var storage = await ConnectAsync();
        var devices = Assert.IsType<VirtualFolder>(storage.Children["Devices"]);
        var motor = devices.Children["Motor"];

        // Act
        await devices.WriteChildMetadataAsync(motor, new SubjectMetadata("Pump", "Water", null, null), CancellationToken.None);
        var written = motor.TryGetSubjectMetadata();
        await storage.ReconcileAsync(Named("Devices/Motor.metadata.json"));

        // Assert
        Assert.Equal(new SubjectMetadata("Pump", "Water", null, null), written);
        Assert.Same(written, motor.TryGetSubjectMetadata());
        Assert.Equal("Pump", ReadMetadataFile("Devices/Motor.metadata.json").NavigationTitle);
    }

    [Fact]
    public async Task WhenSubjectMetadataIsRemoved_ThenItsFileIsDeleted()
    {
        // Arrange
        WriteFile("Motor.json", SerializeMotor("Motor 1"));
        WriteFile("Motor.metadata.json", """{ "navTitle": "Pump" }""");
        var storage = await ConnectAsync();
        var motor = storage.Children["Motor"];

        // Act
        await storage.WriteChildMetadataAsync(motor, new SubjectMetadata(null, null, null, null), CancellationToken.None);

        // Assert
        Assert.False(File.Exists(GetFullPath("Motor.metadata.json")));
        Assert.Null(motor.TryGetSubjectMetadata());
    }

    [Fact]
    public async Task WhenMetadataIsWrittenForAPage_ThenItFails()
    {
        // Arrange
        WriteFile("Readme.md");
        var storage = await ConnectAsync();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            storage.WriteChildMetadataAsync(storage.Children["Readme.md"], new SubjectMetadata("Home", null, null, null), CancellationToken.None));
    }

    [Fact]
    public async Task WhenRootMetadataIsWritten_ThenItIsNotSupported()
    {
        // Arrange
        var storage = await ConnectAsync();

        // Act & Assert
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            storage.WriteMetadataAsync(new FolderMetadata("Root", null, null), CancellationToken.None));
    }

    [Fact]
    public async Task WhenChildIsMovedUp_ThenTheWholeOrderIsWritten()
    {
        // Arrange
        WriteFile("a.md");
        WriteFile("b.md");
        WriteFile("c.md");
        var storage = await ConnectAsync();

        // Act
        await storage.MoveChildAsync("c.md", "b.md", CancellationToken.None);

        // Assert
        string?[] expectedOrder = ["a.md", "c.md", "b.md"];
        Assert.Equal(["a.md", "c.md", "b.md"], storage.ChildOrder.ToArray());
        Assert.Equal(expectedOrder, ReadMetadataFile(".metadata.json").Order);
    }

    [Theory]
    [InlineData("b.md", "d.md", new[] { "a.md", "c.md", "d.md", "b.md", "e.md" })]
    [InlineData("e.md", "b.md", new[] { "a.md", "e.md", "b.md", "c.md", "d.md" })]
    [InlineData("b.md", "a.md", new[] { "b.md", "a.md", "c.md", "d.md", "e.md" })]
    [InlineData("c.md", "a.md", new[] { "c.md", "a.md", "b.md", "d.md", "e.md" })]
    [InlineData("b.md", "e.md", new[] { "a.md", "c.md", "d.md", "e.md", "b.md" })]
    [InlineData("a.md", "e.md", new[] { "b.md", "c.md", "d.md", "e.md", "a.md" })]
    public async Task WhenChildIsMovedToThePlaceOfAnotherChild_ThenTheChildrenInBetweenShift(
        string name, string targetName, string[] expectedOrder)
    {
        // Arrange
        WriteFile("Docs/a.md");
        WriteFile("Docs/b.md");
        WriteFile("Docs/c.md");
        WriteFile("Docs/d.md");
        WriteFile("Docs/e.md");
        var storage = await ConnectAsync();
        var docs = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);

        // Act
        await docs.MoveChildAsync(name, targetName, CancellationToken.None);

        // Assert
        Assert.Equal(expectedOrder, docs.ChildOrder.ToArray());
        Assert.Equal(expectedOrder, ReadMetadataFile("Docs/.metadata.json").Order);
    }

    [Fact]
    public async Task WhenChildIsMovedOntoItself_ThenOrderIsUnchanged()
    {
        // Arrange
        WriteFile("Docs/a.md");
        WriteFile("Docs/b.md");
        WriteFile("Docs/c.md");
        var storage = await ConnectAsync();
        var docs = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);

        // Act
        await docs.MoveChildAsync("b.md", "b.md", CancellationToken.None);

        // Assert
        string?[] expectedOrder = ["a.md", "b.md", "c.md"];
        Assert.Equal(["a.md", "b.md", "c.md"], docs.ChildOrder.ToArray());
        Assert.Equal(expectedOrder, ReadMetadataFile("Docs/.metadata.json").Order);
    }

    [Fact]
    public async Task WhenOrderHasNamesOfChildrenThatAreGone_ThenMoveDropsThem()
    {
        // Arrange
        WriteFile("Docs/a.md");
        WriteFile("Docs/b.md");
        WriteFile("Docs/.metadata.json", """{ "order": ["gone.md", "b.md", "a.md"] }""");
        var storage = await ConnectAsync();
        var docs = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);

        // Act
        await docs.MoveChildAsync("a.md", "b.md", CancellationToken.None);

        // Assert
        Assert.Equal(["a.md", "b.md"], docs.ChildOrder.ToArray());
    }

    [Fact]
    public async Task WhenTwoMovesAreStartedAtOnce_ThenBothApply()
    {
        // Arrange
        WriteFile("a.md");
        WriteFile("b.md");
        WriteFile("c.md");
        var storage = await ConnectAsync();

        // Act
        await Task.WhenAll(
            storage.MoveChildAsync("b.md", "a.md", CancellationToken.None),
            storage.MoveChildAsync("c.md", "a.md", CancellationToken.None));

        // Assert
        Assert.Equal(["b.md", "c.md", "a.md"], storage.ChildOrder.ToArray());
    }

    [Fact]
    public async Task WhenMovedNameIsNoChild_ThenMoveFailsNamingIt()
    {
        // Arrange
        WriteFile("a.md");
        var storage = await ConnectAsync();

        // Act
        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            storage.MoveChildAsync("a.md", "gone.md", CancellationToken.None));

        // Assert
        Assert.Contains("'gone.md'", exception.Message);
        Assert.Contains("Its children: 'a.md'", exception.Message);
        Assert.Equal("targetName", exception.ParamName);
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

    [Fact]
    public async Task WhenSubjectNameEndsWithMetadata_ThenAddingItFails()
    {
        // Arrange
        var storage = await ConnectAsync();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            storage.AddSubjectAsync("Motor.metadata.json", CreateMotor(), CancellationToken.None));
    }
}
