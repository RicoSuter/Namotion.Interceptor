using System.Text.Json;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Components.Abstractions.Pages;
using HomeBlaze.History;
using HomeBlaze.OpcUa;
using HomeBlaze.Services;
using HomeBlaze.Storage.Internal;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Storage.Tests;

public class MetadataSubjectTests : StorageTestBase
{
    private static JsonElement Values(string json) => JsonElement.Parse(json);

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
    public async Task WhenMetadataIsSetAndReadThroughTheMetadataSubjectOfASubject_ThenTheSubjectHasIt()
    {
        // Arrange
        WriteFile("Motor.json", SerializeMotor("Motor 1"));
        var storage = await ConnectAsync();
        var motor = storage.Children["Motor"];
        var metadataSubject = GetMetadataSubject(motor);

        // Act
        await metadataSubject.SetMetadataAsync(Values("""{ "icon": "Water" }"""), CancellationToken.None);
        var metadata = await metadataSubject.GetMetadataAsync(CancellationToken.None);

        // Assert
        Assert.Equal("""{"icon":"Water"}""", metadata.GetRawText());
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
        await metadataSubject.SetOrderAsync(["b.md", "a.md"], CancellationToken.None);
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
    public async Task WhenACallFailsForAnotherReasonThanARefusalOfTheStorage_ThenItKeepsItsType()
    {
        // Arrange: a call from a lifecycle handler runs on the worker, which refuses a call from within its own work.
        WriteFile("Motor.json", SerializeMotor("Motor 1"));
        var storage = await ConnectAsync();
        var motor = storage.Children["Motor"];
        var handler = new CallOnDetach(motor, () => GetMetadataSubject(storage).GetMetadataAsync(CancellationToken.None));
        Context!.AddService<ILifecycleHandler>(handler);

        // Act
        await storage.DeleteSubjectAsync(motor, CancellationToken.None);

        // Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => handler.Call!);
        Assert.Equal(StorageWorker.CallFromWithinItemMessage, exception.Message);
    }

    [Fact]
    public async Task WhenTheMetadataSubjectIsListed_ThenItsMethodsAreForAgentsOnly()
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
    }

    [Theory]
    [InlineData("get")]
    [InlineData("set")]
    [InlineData("order")]
    public async Task WhenAStringValueOfTheFileHasALoneSurrogate_ThenTheFileLoadsAndAGetOrAWriteIsRefusedNamingTheFile(string call)
    {
        // Arrange
        const string content = """{ "navTitle": "Pump", "note": { "text": "\ud800" } }""";
        WriteFile("a.md");
        WriteFile("b.md");
        WriteFile("Motor.json", SerializeMotor("Motor 1"));
        WriteFile("Motor.metadata.json", content);
        WriteFile(".metadata.json", content);
        var storage = await ConnectAsync();
        var motor = storage.Children["Motor"];
        Func<Task> act = call switch
        {
            "get" => () => GetMetadataSubject(motor).GetMetadataAsync(CancellationToken.None),
            "set" => () => GetMetadataSubject(motor).SetMetadataAsync(Values("""{ "icon": "Water" }"""), CancellationToken.None),
            _ => () => GetMetadataSubject(storage).SetOrderAsync(["b.md"], CancellationToken.None)
        };
        var path = call == "order" ? ".metadata.json" : "Motor.metadata.json";

        // Act
        var exception = await Assert.ThrowsAsync<ArgumentException>(act);

        // Assert
        Assert.Contains($"'{path}' could not be read", exception.Message);
        Assert.Equal("Pump", motor.TryGetSubjectMetadata()?.NavigationTitle);
        Assert.Equal(content, File.ReadAllText(GetFullPath(path)));
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
        Assert.Equal("Pump", ReadMetadataFile("Motor.metadata.json").SubjectMetadata?.NavigationTitle);
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
            Assert.Equal("Archive", ReadMetadataFile("Nested.metadata.json").SubjectMetadata?.NavigationTitle);
            Assert.Equal(["b.md", "a.md"], nested.ChildOrder.ToArray());
            Assert.True(File.Exists(Path.Combine(nestedDirectory.FullName, ".metadata.json")));
            Assert.False(File.Exists(GetFullPath(".metadata.json")));
        }
        finally
        {
            nested.Dispose();
        }
    }

    [Fact]
    public async Task WhenANestedStorageIsRemovedFromTheOuterStorage_ThenItsMetadataSubjectWritesNothing()
    {
        // Arrange
        var nestedDirectory = CreateTemporaryDirectory();
        File.WriteAllText(Path.Combine(nestedDirectory.FullName, "a.md"), "a");
        WriteFile("Nested.json", SerializeStorage(nestedDirectory.FullName));
        var storage = await ConnectAsync();
        var nested = Assert.IsType<FluentStorageContainer>(storage.Children["Nested"]);
        await nested.ConnectAsync(CancellationToken.None);
        try
        {
            var metadataSubject = GetMetadataSubject(nested);
            File.Delete(GetFullPath("Nested.json"));
            await storage.ReconcileAsync(Named("Nested.json"));

            // Act
            var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
                metadataSubject.SetMetadataAsync(Values("""{ "title": "Archive" }"""), CancellationToken.None));

            // Assert
            Assert.False(storage.Children.ContainsKey("Nested"));
            Assert.Contains("no longer in a storage", exception.Message);
            Assert.False(File.Exists(Path.Combine(nestedDirectory.FullName, ".metadata.json")));
        }
        finally
        {
            nested.Dispose();
        }
    }

    [Fact]
    public async Task WhenTheOpcUaServerWalksAStoredSubject_ThenItSkipsTheMetadataSubjectAndKeepsTheState()
    {
        // Arrange
        WriteFile("Motor.json", SerializeMotor("Motor 1"));
        var storage = await ConnectAsync();
        var registered = storage.Children["Motor"].TryGetRegisteredSubject()!;
        var provider = new StateAttributeOpcUaPathProvider();

        // Act
        var isMetadataIncluded = provider.IsPropertyIncluded(registered.TryGetProperty(IMetadataSubject.PropertyName)!);
        var isStateIncluded = provider.IsPropertyIncluded(registered.TryGetProperty(nameof(Samples.Motor.CurrentSpeed))!);

        // Assert
        Assert.False(isMetadataIncluded);
        Assert.True(isStateIncluded);
    }

    [Fact]
    public void WhenTheMetadataSubjectOfAnOwnerIsAttached_ThenAnOwnerInAnotherContextGetsItsOwnMeanwhile()
    {
        // Arrange: a context runs its attach handlers one at a time, so the other owner is in a context of its own.
        CreateStorage();
        var first = CreateMotor("First");
        var firstContext = Context!;
        CreateStorage();
        var second = CreateMotor("Second");
        var handler = new EnsureOnAttach(first, () => MetadataSubject.Ensure(second));
        firstContext.AddService<ILifecycleHandler>(handler);

        // Act
        var metadataSubject = MetadataSubject.Ensure(first);

        // Assert
        Assert.NotNull(metadataSubject);
        Assert.True(handler.IsEnsuredMeanwhile);
        Assert.NotNull(second.TryGetMetadataSubject());
    }

    // Ensures the metadata subject of another owner on another thread while the one of the owner is attached.
    private sealed class EnsureOnAttach(IInterceptorSubject owner, Action ensure) : ILifecycleHandler
    {
        public bool IsEnsuredMeanwhile { get; private set; }

        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            if (change.IsContextAttach && change.Subject is MetadataSubject metadataSubject && ReferenceEquals(metadataSubject.GetOwner(), owner))
            {
                IsEnsuredMeanwhile = Task.Run(ensure).Wait(DeadlockTimeout);
            }
        }
    }

    private sealed class CallOnDetach(IInterceptorSubject subject, Func<Task> call) : ILifecycleHandler
    {
        public Task? Call { get; private set; }

        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            if (change.IsContextDetach && ReferenceEquals(change.Subject, subject))
            {
                Call = call();
            }
        }
    }
}
