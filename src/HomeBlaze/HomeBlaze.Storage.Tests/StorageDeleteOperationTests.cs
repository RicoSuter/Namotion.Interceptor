using System.Text.Json;
using FluentStorage.Storage;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Authorization;
using HomeBlaze.Services;
using HomeBlaze.Storage.Tests.Metadata;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;

namespace HomeBlaze.Storage.Tests;

public class StorageDeleteOperationTests : StorageTestBase
{
    private static MethodMetadata? GetDeleteOperation(IInterceptorSubject subject)
        => subject.TryGetRegisteredSubject()!.GetDeleteOperation(ExclusionTarget.None);

    [Fact]
    public async Task WhenASubjectIsLoadedFromAFile_ThenItHasADeleteOperationThatRequiresConfirmationAndAdministration()
    {
        // Arrange
        WriteFile("Motor.json", SerializeMotor("Motor"));
        WriteFile("Page.md", "# Page");

        // Act
        var storage = await ConnectAsync();

        // Assert
        var motorDelete = GetDeleteOperation(storage.Children["Motor"]);
        Assert.NotNull(motorDelete);
        Assert.Equal("Delete", motorDelete.PropertyName);
        Assert.Equal(OperationPurpose.Delete, motorDelete.Purpose);
        Assert.True(motorDelete.RequiresConfirmation);
        Assert.True(motorDelete.RequiresAdministration);
        Assert.DoesNotContain(motorDelete.Parameters, parameter => parameter.RequiresInput);
        Assert.NotNull(GetDeleteOperation(storage.Children["Page.md"]));
    }

    [Fact]
    public async Task WhenTheDeleteOperationOfAFileAndAFolderIsTagged_ThenItIsTaggedAdministration()
    {
        // Arrange
        WriteFile("House/Motor.json", SerializeMotor("Motor"));
        var storage = await ConnectAsync();
        var house = Assert.IsType<VirtualFolder>(storage.Children["House"]);
        var tagProvider = new HomeBlazeMemberTagProvider();

        // Act
        var fileTags = tagProvider.GetTags(house.Children["Motor"].TryGetRegisteredSubject()!.TryGetProperty("Delete")!);
        var folderTags = tagProvider.GetTags(house.TryGetRegisteredSubject()!.TryGetProperty("Delete")!);

        // Assert
        Assert.Contains(MemberTags.Administration, fileTags);
        Assert.Contains(MemberTags.Operation, fileTags);
        Assert.Contains(MemberTags.RequiresConfirmation, fileTags);
        Assert.Contains(MemberTags.Administration, folderTags);
    }

    [Fact]
    public async Task WhenASubjectIsAdded_ThenItHasADeleteOperation()
    {
        // Arrange
        var storage = await ConnectAsync();
        var motor = CreateMotor();

        // Act
        await storage.AddSubjectAsync("Added.json", motor, CancellationToken.None);

        // Assert
        Assert.NotNull(GetDeleteOperation(motor));
    }

    [Fact]
    public async Task WhenTheStorageHoldsFoldersAndSubjectsInsideFiles_ThenOnlyTheStoredSubjectsAndFoldersHaveADeleteOperation()
    {
        // Arrange
        WriteFile("House/Motor.json", SerializeMotor("Motor"));
        WriteFile("Shelly.json", MetadataTestDevice.Serialize());

        // Act
        var storage = await ConnectAsync();

        // Assert
        var house = Assert.IsType<VirtualFolder>(storage.Children["House"]);
        var device = Assert.IsType<MetadataTestDevice>(storage.Children["Shelly"]);
        Assert.Null(GetDeleteOperation(storage));
        Assert.NotNull(GetDeleteOperation(house));
        Assert.NotNull(GetDeleteOperation(house.Children["Motor"]));
        Assert.NotNull(GetDeleteOperation(device));
        Assert.Null(GetDeleteOperation(device.Channel!));
        Assert.Null(GetDeleteOperation(device.Channels[0]));
    }

    [Fact]
    public async Task WhenAStorageIsStoredInAnotherStorage_ThenItHasADeleteOperation()
    {
        // Arrange
        var innerDirectory = CreateTemporaryDirectory();
        WriteFile("Inner.json", SerializeStorage(innerDirectory.FullName));

        // Act
        var storage = await ConnectAsync();

        // Assert
        var inner = Assert.IsType<FluentStorageContainer>(storage.Children["Inner"]);
        Assert.NotNull(GetDeleteOperation(inner));
    }

    [Fact]
    public async Task WhenAStoredSubjectHasItsOwnDeleteOperation_ThenTheStorageAddsNone()
    {
        // Arrange
        WriteFile("Device.json", OwnDeleteDevice.Serialize("Device"));

        // Act
        var storage = await ConnectAsync();

        // Assert
        var device = storage.Children["Device"];
        Assert.Equal("Unpair", GetDeleteOperation(device)?.Title);
        Assert.Null(device.TryGetRegisteredSubject()!.TryGetProperty("Delete"));
    }

    [Fact]
    public async Task WhenTheDeleteOperationIsInvoked_ThenTheFileAndTheSubjectAreGone()
    {
        // Arrange
        WriteFile("Motor.json", SerializeMotor("Motor"));
        var storage = await ConnectAsync();
        var deleteOperation = GetDeleteOperation(storage.Children["Motor"])!;

        // Act
        await deleteOperation.InvokeAsync(null, null, CancellationToken.None);

        // Assert
        Assert.False(File.Exists(GetFullPath("Motor.json")));
        Assert.False(storage.Children.ContainsKey("Motor"));
    }

    [Fact]
    public async Task WhenASubjectIsAddedToNewNestedFolders_ThenItsDeleteOperationDeletesItsFile()
    {
        // Arrange
        var storage = await ConnectAsync();
        var motor = CreateMotor();
        await storage.AddSubjectAsync("demo/motors/motor1.json", motor, CancellationToken.None);
        var deleteOperation = GetDeleteOperation(motor);

        // Act
        Assert.NotNull(deleteOperation);
        await deleteOperation.InvokeAsync(null, null, CancellationToken.None);

        // Assert
        Assert.False(File.Exists(GetFullPath("demo/motors/motor1.json")));
    }

    [Fact]
    public async Task WhenAStoredSubjectIsMovedToAnotherFolder_ThenItsDeleteOperationDeletesTheMovedFile()
    {
        // Arrange
        WriteFile("Motor.json", SerializeMotor("Pump"));
        var storage = await ConnectAsync();
        var motor = storage.Children["Motor"];
        Directory.CreateDirectory(GetFullPath("Devices"));
        File.Move(GetFullPath("Motor.json"), GetFullPath("Devices/Engine.json"));
        await storage.ReconcileAsync();
        var devices = Assert.IsType<VirtualFolder>(storage.Children["Devices"]);
        Assert.Same(motor, devices.Children["Engine"]);
        var deleteOperation = GetDeleteOperation(motor);

        // Act
        Assert.NotNull(deleteOperation);
        await deleteOperation.InvokeAsync(null, null, CancellationToken.None);

        // Assert
        Assert.False(File.Exists(GetFullPath("Devices/Engine.json")));
        Assert.False(devices.Children.ContainsKey("Engine"));
    }

    [Fact]
    public async Task WhenAFolderIsEmpty_ThenItsDeleteOperationIsEnabled()
    {
        // Arrange
        Directory.CreateDirectory(GetFullPath("Empty"));

        // Act
        var storage = await ConnectAsync();

        // Assert
        var empty = Assert.IsType<VirtualFolder>(storage.Children["Empty"]);
        Assert.True(empty.TryGetRegisteredSubject()!.TryGetProperty("Delete")!.IsMethodEnabled());
    }

    [Fact]
    public async Task WhenAFolderHasChildren_ThenItsDeleteOperationIsDisabled()
    {
        // Arrange
        WriteFile("House/Motor.json", SerializeMotor("Motor"));

        // Act
        var storage = await ConnectAsync();

        // Assert
        var house = Assert.IsType<VirtualFolder>(storage.Children["House"]);
        Assert.False(house.TryGetRegisteredSubject()!.TryGetProperty("Delete")!.IsMethodEnabled());
    }

    [Fact]
    public async Task WhenAnEmptyFolderIsDeleted_ThenItsDirectoryAndMetadataFileAreGone()
    {
        // Arrange
        Directory.CreateDirectory(GetFullPath("Empty"));
        WriteFile("Empty/.metadata.json", """{ "navTitle": "Empty" }""");
        var storage = await ConnectAsync();
        var empty = storage.Children["Empty"];
        var deleteOperation = GetDeleteOperation(empty)!;

        // Act
        await deleteOperation.InvokeAsync(null, null, CancellationToken.None);

        // Assert
        Assert.False(Directory.Exists(GetFullPath("Empty")));
        Assert.False(storage.Children.ContainsKey("Empty"));
    }

    [Fact]
    public async Task WhenAFolderWithChildrenIsDeleted_ThenItThrowsAndKeepsTheFolder()
    {
        // Arrange
        WriteFile("House/Motor.json", SerializeMotor("Motor"));
        var storage = await ConnectAsync();
        var house = storage.Children["House"];

        // Act
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => storage.DeleteSubjectAsync(house, CancellationToken.None));

        // Assert
        Assert.Contains("not empty", exception.Message);
        Assert.True(File.Exists(GetFullPath("House/Motor.json")));
        Assert.Same(house, storage.Children["House"]);
    }

    [Fact]
    public async Task WhenAFileAppearedInAnEmptyFolderSinceTheLastPass_ThenDeletingTheFolderIsRefusedAndKeepsTheFolderAndItsMetadata()
    {
        // Arrange
        WriteFile("Empty/.metadata.json", """{ "title": "Kept" }""");
        var storage = await ConnectAsync();
        var empty = Assert.IsType<VirtualFolder>(storage.Children["Empty"]);
        WriteFile("Empty/Late.txt");

        // Act
        var exception = await Record.ExceptionAsync(() => storage.DeleteSubjectAsync(empty, CancellationToken.None));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
        Assert.True(File.Exists(GetFullPath("Empty/Late.txt")));
        Assert.True(File.Exists(GetFullPath("Empty/.metadata.json")));
        Assert.Same(empty, storage.Children["Empty"]);
        Assert.Equal("Kept", empty.Title);
    }

    [Fact]
    public async Task WhenAFolderHoldsOnlyAnEmptyFolder_ThenItsDeleteOperationIsDisabledAndTheDeleteIsRefused()
    {
        // Arrange
        Directory.CreateDirectory(GetFullPath("Outer/Inner"));
        var storage = await ConnectAsync();
        var outer = Assert.IsType<VirtualFolder>(storage.Children["Outer"]);

        // Act
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => storage.DeleteSubjectAsync(outer, CancellationToken.None));

        // Assert
        Assert.False(outer.TryGetRegisteredSubject()!.TryGetProperty("Delete")!.IsMethodEnabled());
        Assert.Contains("not empty", exception.Message);
        Assert.True(Directory.Exists(GetFullPath("Outer/Inner")));
    }

    [Fact]
    public async Task WhenTheLastChildOfAFolderIsDeleted_ThenItsDeleteOperationIsEnabled()
    {
        // Arrange
        WriteFile("House/Motor.json", SerializeMotor("Motor"));
        var storage = await ConnectAsync();
        var house = Assert.IsType<VirtualFolder>(storage.Children["House"]);
        var deleteProperty = house.TryGetRegisteredSubject()!.TryGetProperty("Delete")!;
        Assert.False(deleteProperty.IsMethodEnabled());

        // Act
        await storage.DeleteSubjectAsync(house.Children["Motor"], CancellationToken.None);

        // Assert
        Assert.True(deleteProperty.IsMethodEnabled());
    }

    [Fact]
    public async Task WhenAnEmptyFolderIsDeleted_ThenItDoesNotComeBackWithAReconcile()
    {
        // Arrange
        Directory.CreateDirectory(GetFullPath("Empty"));
        var storage = await ConnectAsync();

        // Act
        await storage.DeleteSubjectAsync(storage.Children["Empty"], CancellationToken.None);
        await storage.ReconcileAsync();

        // Assert
        Assert.False(storage.Children.ContainsKey("Empty"));
        Assert.False(Directory.Exists(GetFullPath("Empty")));
    }

    [Theory]
    [InlineData(".gitkeep")]
    [InlineData("Scratch.tmp")]
    [InlineData(".git/HEAD")]
    public async Task WhenAFolderHoldsHiddenFiles_ThenItIsRefusedAndKeepsItsFilesAndMetadata(string hiddenFile)
    {
        // Arrange
        WriteFile("Hidden/.metadata.json", """{ "title": "Kept" }""");
        WriteFile($"Hidden/{hiddenFile}");
        var storage = await ConnectAsync();
        var hidden = Assert.IsType<VirtualFolder>(storage.Children["Hidden"]);

        // Act
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => storage.DeleteSubjectAsync(hidden, CancellationToken.None));

        // Assert
        Assert.Contains("not shown", exception.Message);
        Assert.True(File.Exists(GetFullPath("Hidden/.metadata.json")));
        Assert.True(File.Exists(GetFullPath($"Hidden/{hiddenFile}")));
        Assert.Same(hidden, storage.Children["Hidden"]);
        Assert.Equal("Kept", hidden.Title);
    }

    [Fact]
    public async Task WhenTheDirectoryCannotBeDeletedAfterItsMetadataFile_ThenTheMetadataFileIsWrittenBack()
    {
        // Arrange
        const string metadata = """{ "title": "Kept" }""";
        WriteFile("Empty/.metadata.json", metadata);
        PausableStore? client = null;
        var storage = await ConnectAsync(configure: container => container.ClientDecorator = inner => client = new PausableStore(inner));
        var empty = Assert.IsType<VirtualFolder>(storage.Children["Empty"]);
        client!.FailNextDeleteDirectory(new IOException("The directory is in use."));

        // Act
        var exception = await Record.ExceptionAsync(() => storage.DeleteSubjectAsync(empty, CancellationToken.None));

        // Assert
        Assert.IsType<IOException>(exception);
        Assert.Equal(metadata, File.ReadAllText(GetFullPath("Empty/.metadata.json")));
        Assert.Same(empty, storage.Children["Empty"]);
        Assert.Equal("Kept", empty.Title);
    }

    [Fact]
    public async Task WhenAnInMemoryFolderHoldsOnlyItsMetadataFile_ThenItIsDeletedWithoutADirectoryDeleteAndStaysGone()
    {
        // Arrange
        PausableStore? client = null;
        var storage = await ConnectAsync(configure: container =>
        {
            container.StorageType = "inmemory";
            container.ClientDecorator = inner => client = new PausableStore(inner);
        });
        await storage.AddSubjectAsync("House/Motor.json", CreateMotor(), CancellationToken.None);
        var house = Assert.IsType<VirtualFolder>(storage.Children["House"]);
        await storage.SetMetadataAsync(house, null, JsonDocument.Parse("""{ "title": "House" }""").RootElement, CancellationToken.None);
        await storage.DeleteSubjectAsync(house.Children["Motor"], CancellationToken.None);
        Assert.True(await storage.Connection.Client.ObjectExists("House/.metadata.json"));
        client!.FailNextDeleteDirectory(new InvalidOperationException("A bucket has no directories to delete."));

        // Act
        await storage.DeleteSubjectAsync(house, CancellationToken.None);
        await storage.ReconcileAsync();

        // Assert
        Assert.Equal(0, client.GetCallCount(nameof(IStore.DeleteDirectory)));
        Assert.False(await storage.Connection.Client.ObjectExists("House/.metadata.json"));
        Assert.False(storage.Children.ContainsKey("House"));
    }
}
