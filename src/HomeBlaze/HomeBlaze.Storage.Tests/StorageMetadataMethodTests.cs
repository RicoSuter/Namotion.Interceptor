using System.Text;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Components.Abstractions.Pages;
using HomeBlaze.Services;
using HomeBlaze.Storage.Internal;
using Namotion.Interceptor.Registry;

namespace HomeBlaze.Storage.Tests;

public class StorageMetadataMethodTests : StorageTestBase
{
    private StorageMetadataDocument ReadMetadataFile(string relativePath)
        => StorageMetadataDocument.Parse(File.ReadAllBytes(GetFullPath(relativePath)));

    [Fact]
    public async Task WhenFolderIsLoaded_ThenItsOrderAndMetadataMethodsAreOnlyForAgents()
    {
        // Arrange
        WriteFile("Docs/a.md");
        var storage = await ConnectAsync();
        var docs = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);

        // Act
        var folderMethodsForPeople = docs.TryGetRegisteredSubject()!.GetAllMethods(MethodAudience.People);
        var folderMethodsForAgents = docs.TryGetRegisteredSubject()!.GetAllMethods(MethodAudience.Agents);
        var rootMethodsForPeople = storage.TryGetRegisteredSubject()!.GetAllMethods(MethodAudience.People);
        var rootMethodsForAgents = storage.TryGetRegisteredSubject()!.GetAllMethods(MethodAudience.Agents);

        // Assert
        Assert.Equal(["Create", "Create Markdown File"], folderMethodsForPeople.Select(method => method.Title));
        Assert.Equal(
            ["Create", "Create Markdown File", "Get Order", "Set Order", "Get Metadata", "Set Metadata", "Set Child Metadata"],
            folderMethodsForAgents.Select(method => method.Title));
        Assert.Equal(["Create", "Create Markdown File"], rootMethodsForPeople.Select(method => method.Title));
        Assert.Equal(
            ["Create", "Create Markdown File", "Get Order", "Set Order", "Get Metadata", "Set Child Metadata"],
            rootMethodsForAgents.Select(method => method.Title));
    }

    [Fact]
    public async Task WhenOrderIsSetAndReadThroughTheMethods_ThenTheResultIsTheStoredOrder()
    {
        // Arrange
        WriteFile("Docs/a.md");
        WriteFile("Docs/b.md");
        var storage = await ConnectAsync();
        var registered = storage.Children["Docs"].TryGetRegisteredSubject()!;
        var setOrder = registered.TryGetProperty("SetOrder")!.TryGetMethodMetadata()!;
        var getOrder = registered.TryGetProperty("GetOrder")!.TryGetMethodMetadata()!;

        // Act
        await setOrder.InvokeAsync([new[] { "b.md" }], serviceProvider: null, CancellationToken.None);
        var order = await getOrder.InvokeAsync([], serviceProvider: null, CancellationToken.None);

        // Assert
        Assert.Equal(["b.md", "a.md"], Assert.IsType<string[]>(order));
    }

    [Fact]
    public async Task WhenChildMetadataMethodIsListed_ThenOnlyTheNameIsRequired()
    {
        // Arrange
        WriteFile("Docs/a.md");
        var storage = await ConnectAsync();
        var registered = storage.Children["Docs"].TryGetRegisteredSubject()!;

        // Act
        var parameters = registered.TryGetProperty("SetChildMetadata")!.TryGetMethodMetadata()!.Parameters
            .Where(parameter => parameter.RequiresInput)
            .ToArray();

        // Assert
        Assert.Equal(["name", "navigationTitle", "icon", "location", "alignment"], parameters.Select(parameter => parameter.Name));
        Assert.Equal([false, true, true, true, true], parameters.Select(parameter => parameter.IsNullable));
    }

    [Fact]
    public async Task WhenOrderIsRead_ThenTheChildrenAreInTheOrderInWhichTheyAreShown()
    {
        // Arrange
        WriteFile("Docs/a.md");
        WriteFile("Docs/b.md");
        WriteFile("Docs/c.md");
        WriteFile("Docs/.metadata.json", """{ "order": ["c.md", "gone.md"] }""");
        var storage = await ConnectAsync();
        var docs = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);

        // Act
        var order = await docs.GetOrderAsync(CancellationToken.None);

        // Assert
        Assert.Equal(["c.md", "a.md", "b.md"], order);
    }

    [Fact]
    public async Task WhenOrderOfTheRootIsRead_ThenTheTopLevelIsInTheOrderInWhichItIsShown()
    {
        // Arrange
        WriteFile("a.md");
        WriteFile("Readme.md");
        WriteFile("Docs/b.md");
        var storage = await ConnectAsync();

        // Act
        var order = await storage.GetOrderAsync(CancellationToken.None);

        // Assert
        Assert.Equal(["Readme.md", "a.md", "Docs"], order);
    }

    [Fact]
    public async Task WhenOrderIsSet_ThenTheNamesComeFirstAndTheOtherChildrenKeepTheirOrder()
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
        await docs.SetOrderAsync(["b.md", "d.md"], CancellationToken.None);

        // Assert
        string?[] expectedOrder = ["b.md", "d.md", "c.md", "a.md"];
        Assert.Equal(["b.md", "d.md", "c.md", "a.md"], docs.ChildOrder.ToArray());
        Assert.Equal(expectedOrder, ReadMetadataFile("Docs/.metadata.json").Order);
        Assert.True(ReadMetadataFile("Docs/.metadata.json").ExtensionData!.ContainsKey("color"));
        Assert.Equal(["b.md", "d.md", "c.md", "a.md"], await docs.GetOrderAsync(CancellationToken.None));
    }

    [Fact]
    public async Task WhenOrderOfTheRootIsSet_ThenItIsWrittenToTheMetadataFileOfTheRoot()
    {
        // Arrange
        WriteFile("a.md");
        WriteFile("b.md");
        WriteFile("c.md");
        var storage = await ConnectAsync();

        // Act
        await storage.SetOrderAsync(["c.md"], CancellationToken.None);

        // Assert
        string?[] expectedOrder = ["c.md", "a.md", "b.md"];
        Assert.Equal(["c.md", "a.md", "b.md"], storage.ChildOrder.ToArray());
        Assert.Equal(expectedOrder, ReadMetadataFile(".metadata.json").Order);
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
            docs.SetOrderAsync(["b.md", "gone.md"], CancellationToken.None));

        // Assert
        Assert.Contains("'gone.md'", exception.Message);
        Assert.Contains("'a.md', 'b.md'", exception.Message);
        Assert.False(File.Exists(GetFullPath("Docs/.metadata.json")));
        Assert.Empty(docs.ChildOrder.ToArray());
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
            docs.SetOrderAsync(["b.md", "a.md", "b.md"], CancellationToken.None));

        // Assert
        Assert.Contains("'b.md'", exception.Message);
        Assert.DoesNotContain("'a.md'", exception.Message);
        Assert.False(File.Exists(GetFullPath("Docs/.metadata.json")));
    }

    [Fact]
    public async Task WhenFolderMetadataFileCannotBeRead_ThenSettingTheOrderFailsAndKeepsTheFile()
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
            docs.SetOrderAsync(["b.md"], CancellationToken.None));
        Assert.Equal(Encoding.UTF8.GetBytes(content), File.ReadAllBytes(GetFullPath("Docs/.metadata.json")));
    }

    [Fact]
    public async Task WhenChildMetadataFileCannotBeRead_ThenSettingTheChildMetadataFailsAndKeepsTheFile()
    {
        // Arrange
        const string content = """{"navTitle":"Pump",}x""";
        WriteFile("Devices/Motor.json", SerializeMotor("Motor 1"));
        WriteFile("Devices/Motor.metadata.json", content);
        var storage = await ConnectAsync();
        var devices = Assert.IsType<VirtualFolder>(storage.Children["Devices"]);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            devices.SetChildMetadataAsync("Motor", "Water", null, null, null, CancellationToken.None));
        Assert.Equal(Encoding.UTF8.GetBytes(content), File.ReadAllBytes(GetFullPath("Devices/Motor.metadata.json")));
    }

    [Fact]
    public async Task WhenMetadataIsRead_ThenItHasTheFolderAndItsChildrenStoredAsJson()
    {
        // Arrange
        WriteFile("Devices/Readme.md");
        WriteFile("Devices/Motor.json", SerializeMotor("Motor 1"));
        WriteFile("Devices/Pump.json", SerializeMotor("Pump 1"));
        WriteFile("Devices/Pump.metadata.json", """{ "navTitle": "Water", "icon": "Water", "location": "AppBar", "alignment": "Right" }""");
        WriteFile("Devices/.metadata.json", """{ "title": "All Devices", "navTitle": "Devices", "icon": "Devices" }""");
        WriteFile("Devices/Sub/a.md");
        var storage = await ConnectAsync();
        var devices = Assert.IsType<VirtualFolder>(storage.Children["Devices"]);

        // Act
        var metadata = await devices.GetMetadataAsync(CancellationToken.None);

        // Assert
        Assert.Equal(new FolderMetadata("All Devices", "Devices", "Devices"), metadata.Metadata);
        Assert.Equal(["Motor", "Pump"], metadata.Children.Keys.Order(StringComparer.Ordinal));
        Assert.Null(metadata.Children["Motor"]);
        Assert.Equal(new SubjectMetadata("Water", "Water", NavigationLocation.AppBar, AppBarAlignment.Right), metadata.Children["Pump"]);
    }

    [Fact]
    public async Task WhenMetadataOfTheRootIsRead_ThenItHasNoMetadataOfItsOwn()
    {
        // Arrange
        WriteFile("Motor.json", SerializeMotor("Motor 1"));
        WriteFile("Motor.metadata.json", """{ "navTitle": "Pump" }""");
        var storage = await ConnectAsync();

        // Act
        var metadata = await storage.GetMetadataAsync(CancellationToken.None);

        // Assert
        Assert.Null(metadata.Metadata);
        Assert.Equal("Pump", Assert.Single(metadata.Children).Value?.NavigationTitle);
    }

    [Fact]
    public async Task WhenFolderMetadataIsSet_ThenEveryValueIsReplacedAndBlankOnesAreRemoved()
    {
        // Arrange
        WriteFile("Docs/a.md");
        WriteFile("Docs/.metadata.json", """{ "title": "Documentation", "navTitle": "Docs", "order": ["a.md"] }""");
        var storage = await ConnectAsync();
        var docs = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);

        // Act
        await docs.SetMetadataAsync(null, " ", "MenuBook", CancellationToken.None);

        // Assert
        Assert.Equal(new FolderMetadata(null, null, "MenuBook"), docs.Metadata);
        var document = ReadMetadataFile("Docs/.metadata.json");
        Assert.Null(document.Title);
        Assert.Null(document.NavigationTitle);
        Assert.Equal("MenuBook", document.Icon);
        Assert.Equal(["a.md"], docs.ChildOrder.ToArray());
    }

    [Fact]
    public async Task WhenChildMetadataIsSet_ThenTheChildHasIt()
    {
        // Arrange
        WriteFile("Devices/Motor.json", SerializeMotor("Motor 1"));
        var storage = await ConnectAsync();
        var devices = Assert.IsType<VirtualFolder>(storage.Children["Devices"]);

        // Act
        await devices.SetChildMetadataAsync("Motor", "Pump", "Water", NavigationLocation.AppBar, AppBarAlignment.Left, CancellationToken.None);

        // Assert
        var expected = new SubjectMetadata("Pump", "Water", NavigationLocation.AppBar, AppBarAlignment.Left);
        Assert.Equal(expected, devices.Children["Motor"].TryGetSubjectMetadata());
        Assert.Equal("Pump", ReadMetadataFile("Devices/Motor.metadata.json").NavigationTitle);
        Assert.Equal(expected, (await devices.GetMetadataAsync(CancellationToken.None)).Children["Motor"]);
    }

    [Fact]
    public async Task WhenChildMetadataOfTheRootIsSetWithoutValues_ThenItIsRemoved()
    {
        // Arrange
        WriteFile("Motor.json", SerializeMotor("Motor 1"));
        WriteFile("Motor.metadata.json", """{ "navTitle": "Pump" }""");
        var storage = await ConnectAsync();

        // Act
        await storage.SetChildMetadataAsync("Motor", null, "", null, null, CancellationToken.None);

        // Assert
        Assert.Null(storage.Children["Motor"].TryGetSubjectMetadata());
        Assert.False(File.Exists(GetFullPath("Motor.metadata.json")));
    }

    [Theory]
    [InlineData("Gone")]
    [InlineData("Readme.md")]
    public async Task WhenChildMetadataIsSetForANameWithoutMetadata_ThenItFailsNamingTheChildrenWithMetadata(string name)
    {
        // Arrange
        WriteFile("Devices/Readme.md");
        WriteFile("Devices/Motor.json", SerializeMotor("Motor 1"));
        var storage = await ConnectAsync();
        var devices = Assert.IsType<VirtualFolder>(storage.Children["Devices"]);

        // Act
        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            devices.SetChildMetadataAsync(name, "Pump", null, null, null, CancellationToken.None));

        // Assert
        Assert.Contains($"'{name}'", exception.Message);
        Assert.Contains("Children with metadata: 'Motor'", exception.Message);
        Assert.False(File.Exists(GetFullPath("Devices/Readme.metadata.json")));
    }
}
