using System.Text.Json;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Components.Abstractions.Pages;
using HomeBlaze.History;
using HomeBlaze.Services;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;

namespace HomeBlaze.Storage.Tests;

public class MetadataSubjectTests : StorageTestBase
{
    private static JsonElement Values(string json) => JsonDocument.Parse(json).RootElement;

    private static MetadataSubject GetMetadataSubject(IInterceptorSubject owner)
        => Assert.IsType<MetadataSubject>(owner.TryGetMetadataSubject());

    [Fact]
    public async Task WhenStorageIsLoaded_ThenTheRootEveryFolderAndEveryJsonSubjectHaveTheirOwnMetadataSubject()
    {
        // Arrange
        WriteFile("Docs/Readme.md");
        WriteFile("Docs/Motor.json", SerializeMotor("Motor 1"));

        // Act
        var storage = await ConnectAsync();

        // Assert
        var docs = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);
        MetadataSubject[] metadataSubjects = [GetMetadataSubject(storage), GetMetadataSubject(docs), GetMetadataSubject(docs.Children["Motor"])];
        Assert.Equal(3, metadataSubjects.Distinct().Count());
        Assert.Null(metadataSubjects[2].SubjectMetadata);
        Assert.Null(docs.Children["Readme.md"].TryGetMetadataSubject());
    }

    [Fact]
    public async Task WhenMetadataOfASubjectIsSetAndRead_ThenKeysAreSetRemovedAndKeptAndTheSubjectHasItAtOnce()
    {
        // Arrange
        WriteFile("Motor.json", SerializeMotor("Motor 1"));
        WriteFile("Motor.metadata.json", """{ "navTitle": "Pump", "tags": ["cellar"] }""");
        var storage = await ConnectAsync();
        var motor = storage.Children["Motor"];
        var metadataSubject = GetMetadataSubject(motor);

        // Act
        await metadataSubject.SetMetadataAsync(Values("""{ "navTitle": null, "icon": "Water" }"""), CancellationToken.None);
        var metadata = await metadataSubject.GetMetadataAsync(CancellationToken.None);

        // Assert
        Assert.Equal("""{"tags":["cellar"],"icon":"Water"}""", metadata.GetRawText());
        Assert.Equal(new SubjectMetadata { Icon = "Water" }, motor.TryGetSubjectMetadata());
    }

    [Fact]
    public async Task WhenMetadataOfASubjectWithoutAFileIsRead_ThenItIsAnEmptyObject()
    {
        // Arrange
        WriteFile("Motor.json", SerializeMotor("Motor 1"));
        var storage = await ConnectAsync();

        // Act
        var metadata = await GetMetadataSubject(storage.Children["Motor"]).GetMetadataAsync(CancellationToken.None);

        // Assert
        Assert.Equal("{}", metadata.GetRawText());
    }

    [Fact]
    public async Task WhenMetadataOfAFolderIsRead_ThenTheOrderIsLeftOut()
    {
        // Arrange
        WriteFile("Docs/a.md");
        WriteFile("Docs/.metadata.json", """{ "title": "Docs", "order": ["a.md"], "color": "red" }""");
        var storage = await ConnectAsync();

        // Act
        var metadata = await GetMetadataSubject(storage.Children["Docs"]).GetMetadataAsync(CancellationToken.None);

        // Assert
        Assert.Equal("""{"title":"Docs","color":"red"}""", metadata.GetRawText());
    }

    [Fact]
    public async Task WhenTitleAndIconOfTheRootAreSet_ThenItsFileHasThemAndTheRootDoesNotUseThem()
    {
        // Arrange
        WriteFile("a.md");
        var storage = await ConnectAsync();
        var title = storage.GetNavigationTitle();
        var icon = storage.GetNavigationIconName();

        // Act
        await GetMetadataSubject(storage).SetMetadataAsync(
            Values("""{ "title": "Home", "navTitle": "Home", "icon": "Home" }"""), CancellationToken.None);

        // Assert
        Assert.Equal(
            """{"title":"Home","navTitle":"Home","icon":"Home"}""",
            ReadMetadataFile(".metadata.json").ToJsonElement(includesOrder: true).GetRawText());
        Assert.Null(storage.Metadata);
        Assert.Equal(title, storage.GetNavigationTitle());
        Assert.Equal(icon, storage.GetNavigationIconName());
    }

    [Fact]
    public async Task WhenOrderIsSetAndReadThroughTheMetadataSubjectOfAFolder_ThenTheFolderHasIt()
    {
        // Arrange
        WriteFile("Docs/a.md");
        WriteFile("Docs/b.md");
        var storage = await ConnectAsync();
        var docs = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);
        var metadataSubject = GetMetadataSubject(docs);

        // Act
        await metadataSubject.SetOrderAsync(["b.md"], CancellationToken.None);
        var order = await metadataSubject.GetOrderAsync(CancellationToken.None);

        // Assert
        Assert.Equal(["b.md", "a.md"], order);
        Assert.Equal(["b.md", "a.md"], docs.ChildOrder.ToArray());
    }

    [Fact]
    public async Task WhenOrderIsSetThroughTheMetadataSubjectOfASubject_ThenItIsRefused()
    {
        // Arrange
        WriteFile("Motor.json", SerializeMotor("Motor 1"));
        var storage = await ConnectAsync();

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            GetMetadataSubject(storage.Children["Motor"]).SetOrderAsync(["a.md"], CancellationToken.None));
        Assert.Contains("folder", exception.Message);
    }

    [Fact]
    public async Task WhenTheMetadataSubjectIsListed_ThenItsMethodsAreForAgentsOnlyAndItsTypeIsExcludedEverywhere()
    {
        // Arrange
        WriteFile("Motor.json", SerializeMotor("Motor 1"));
        var storage = await ConnectAsync();
        var registered = GetMetadataSubject(storage.Children["Motor"]).TryGetRegisteredSubject()!;

        // Act
        var methodsForAgents = registered.GetAllMethods(ExclusionTarget.Agents);
        var methodsForTheUserInterface = registered.GetAllMethods(ExclusionTarget.UserInterface);

        // Assert
        Assert.Equal(["Get Metadata", "Set Metadata", "Get Order", "Set Order"], methodsForAgents.Select(method => method.Title));
        Assert.Empty(methodsForTheUserInterface);
        Assert.True(ExcludeFromAttribute.IsExcluded(typeof(MetadataSubject), ExclusionTarget.All));
    }

    [Fact]
    public async Task WhenSetMetadataIsCalledThroughItsMethod_ThenTheValuesArriveAsAJsonObject()
    {
        // Arrange
        WriteFile("Motor.json", SerializeMotor("Motor 1"));
        var storage = await ConnectAsync();
        var motor = storage.Children["Motor"];
        var method = GetMetadataSubject(motor).TryGetRegisteredSubject()!.TryGetProperty("SetMetadata")!.TryGetMethodMetadata()!;

        // Act
        await method.InvokeAsync([Values("""{ "navTitle": "Pump" }""")], serviceProvider: null, CancellationToken.None);

        // Assert
        Assert.Equal("Pump", motor.TryGetSubjectMetadata()?.NavigationTitle);
    }

    [Fact]
    public async Task WhenAStringValueOfTheFileHasALoneSurrogate_ThenTheFileLoadsAndAWriteIsRefused()
    {
        // Arrange
        const string content = """{ "navTitle": "Pump", "note": "\ud800" }""";
        WriteFile("Motor.json", SerializeMotor("Motor 1"));
        WriteFile("Motor.metadata.json", content);
        var storage = await ConnectAsync();
        var motor = storage.Children["Motor"];

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            GetMetadataSubject(motor).SetMetadataAsync(Values("""{ "icon": "Water" }"""), CancellationToken.None));
        Assert.Equal("Pump", motor.TryGetSubjectMetadata()?.NavigationTitle);
        Assert.Equal(content, File.ReadAllText(GetFullPath("Motor.metadata.json")));
    }

    [Fact]
    public async Task WhenHistoryChecksAStoredSubject_ThenNothingOfItsMetadataIsRecorded()
    {
        // Arrange
        WriteFile("Motor.json", SerializeMotor("Motor 1"));
        WriteFile("Motor.metadata.json", """{ "navTitle": "Pump" }""");
        var storage = await ConnectAsync();
        var motor = storage.Children["Motor"];

        // Act
        var hasMetadataPropertyHistory = motor.TryGetRegisteredSubject()!.TryGetProperty(IMetadataSubject.PropertyName)!.HasHistory();
        var hasSubjectMetadataHistory = GetMetadataSubject(motor).TryGetRegisteredSubject()!
            .TryGetProperty(nameof(MetadataSubject.SubjectMetadata))!.HasHistory();

        // Assert
        Assert.False(hasMetadataPropertyHistory);
        Assert.False(hasSubjectMetadataHistory);
    }

    [Fact]
    public async Task WhenSubjectIsReplacedByAnotherType_ThenTheNewSubjectHasANewMetadataSubjectAndTheOldOneWritesNothing()
    {
        // Arrange
        WriteFile("Motor.json", SerializeMotor("Motor 1"));
        WriteFile("Motor.metadata.json", """{ "navTitle": "Pump" }""");
        var storage = await ConnectAsync();
        var oldMetadataSubject = GetMetadataSubject(storage.Children["Motor"]);
        WriteFile("Motor.json", SerializeStorage(CreateTemporaryDirectory().FullName));

        // Act
        await storage.ReconcileAsync(Named("Motor.json"));

        // Assert
        var replaced = Assert.IsType<FluentStorageContainer>(storage.Children["Motor"]);
        Assert.NotSame(oldMetadataSubject, GetMetadataSubject(replaced));
        Assert.Equal("Pump", replaced.TryGetSubjectMetadata()?.NavigationTitle);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            oldMetadataSubject.SetMetadataAsync(Values("""{ "navTitle": "Old" }"""), CancellationToken.None));
        Assert.Equal("Pump", ReadMetadataFile("Motor.metadata.json").ToSubjectMetadata()?.NavigationTitle);
    }

    [Fact]
    public async Task WhenAFolderIsDeletedAndCreatedAgain_ThenTheMetadataSubjectOfTheDeletedFolderWritesNothing()
    {
        // Arrange
        WriteFile("Docs/a.md");
        var storage = await ConnectAsync();
        var deletedFolder = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);
        var staleMetadataSubject = GetMetadataSubject(deletedFolder);
        Directory.Delete(GetFullPath("Docs"), recursive: true);
        await storage.ReconcileAsync();
        WriteFile("Docs/b.md");
        await storage.ReconcileAsync();

        // Act
        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            staleMetadataSubject.SetMetadataAsync(Values("""{ "title": "Old" }"""), CancellationToken.None));

        // Assert
        Assert.NotSame(deletedFolder, storage.Children["Docs"]);
        Assert.Contains("no longer", exception.Message);
        Assert.False(File.Exists(GetFullPath("Docs/.metadata.json")));
    }

    [Fact]
    public async Task WhenAStorageIsTheJsonFileOfAnotherOne_ThenItsMetadataIsTheFileBesideItAndItsOrderIsItsOwn()
    {
        // Arrange
        var nestedDirectory = CreateTemporaryDirectory();
        File.WriteAllText(Path.Combine(nestedDirectory.FullName, "a.md"), "a");
        File.WriteAllText(Path.Combine(nestedDirectory.FullName, "b.md"), "b");
        WriteFile("Nested.json", SerializeStorage(nestedDirectory.FullName));
        var storage = await ConnectAsync();
        var nested = Assert.IsType<FluentStorageContainer>(storage.Children["Nested"]);
        await nested.ConnectAsync(CancellationToken.None);
        try
        {
            var metadataSubject = GetMetadataSubject(nested);

            // Act
            await metadataSubject.SetMetadataAsync(Values("""{ "navTitle": "Archive" }"""), CancellationToken.None);
            await metadataSubject.SetOrderAsync(["b.md"], CancellationToken.None);

            // Assert
            Assert.Equal("Archive", nested.TryGetSubjectMetadata()?.NavigationTitle);
            Assert.Equal("Archive", ReadMetadataFile("Nested.metadata.json").ToSubjectMetadata()?.NavigationTitle);
            Assert.Equal(["b.md", "a.md"], nested.ChildOrder.ToArray());
            Assert.True(File.Exists(Path.Combine(nestedDirectory.FullName, ".metadata.json")));
        }
        finally
        {
            nested.Dispose();
        }
    }
}
