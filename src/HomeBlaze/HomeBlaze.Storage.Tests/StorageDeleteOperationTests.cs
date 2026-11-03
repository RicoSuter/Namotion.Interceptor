using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Metadata;
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
    public async Task WhenASubjectIsLoadedFromAFile_ThenItHasADeleteOperationThatRequiresConfirmation()
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
        Assert.DoesNotContain(motorDelete.Parameters, parameter => parameter.RequiresInput);
        Assert.NotNull(GetDeleteOperation(storage.Children["Page.md"]));
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
    public async Task WhenTheStorageHoldsFoldersAndSubjectsInsideFiles_ThenOnlyTheStoredSubjectsHaveADeleteOperation()
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
        Assert.Null(GetDeleteOperation(house));
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
}
