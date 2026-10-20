using System.Text.Json;
using HomeBlaze.Abstractions;
using HomeBlaze.Components.Abstractions.Pages;
using HomeBlaze.History;
using HomeBlaze.OpcUa;
using HomeBlaze.Services;
using HomeBlaze.Storage.Internal;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Storage.Tests;

public class StorageMetadataApplyTests : StorageTestBase
{
    private const string LoneSurrogateContent = """{ "navTitle": "Pump", "note": { "text": "\ud800" } }""";

    private static JsonElement Values(string json) => JsonElement.Parse(json);

    private string ReadText(string relativePath) => File.ReadAllText(GetFullPath(relativePath));

    private static RegisteredSubjectProperty GetProperty(IInterceptorSubject subject, string name)
        => subject.TryGetRegisteredSubject()!.TryGetProperty(name)!;

    [Fact]
    public async Task WhenASidecarHasEntries_ThenTheStoredSubjectTheSubjectsInsideItAndTheirPropertiesHaveTheirMetadata()
    {
        // Arrange
        WriteFile("Shelly.json", MetadataTestDevice.Serialize());
        WriteFile("Shelly.metadata.json", """
            {
              "navTitle": "Shelly Pro 4PM",
              "$subjects": {
                "Channel": { "navTitle": "Pump relay" },
                "Channels[0]": { "navTitle": "Hall light" },
                "Meters['Main']": { "area": "Utility room" }
              },
              "$properties": {
                "Firmware": { "comment": "auto-update disabled" },
                "Channels/0/Power": { "comment": "includes the hall heater" }
              }
            }
            """);

        // Act
        var storage = await ConnectAsync();

        // Assert
        var device = Assert.IsType<MetadataTestDevice>(storage.Children["Shelly"]);
        Assert.Equal("Shelly Pro 4PM", device.GetMetadata().GetString(MetadataKeys.NavigationTitle));
        Assert.Equal("Pump relay", device.Channel!.GetMetadata().GetString(MetadataKeys.NavigationTitle));
        Assert.Equal("Hall light", device.Channels[0].GetMetadata().GetString(MetadataKeys.NavigationTitle));
        Assert.Null(device.Channels[1].GetMetadata());
        Assert.Equal("Utility room", device.Meters["Main"].GetMetadata().GetString("area"));
        Assert.Equal("auto-update disabled", GetProperty(device, nameof(MetadataTestDevice.Firmware)).GetMetadata().GetString("comment"));
        Assert.Equal("includes the hall heater", GetProperty(device.Channels[0], nameof(MetadataTestChannel.Power)).GetMetadata().GetString("comment"));
        Assert.Null(GetProperty(device.Channels[1], nameof(MetadataTestChannel.Power)).GetMetadata());
    }

    [Fact]
    public async Task WhenAnEntryIsRemovedFromTheFile_ThenItsTargetHasNoMetadata()
    {
        // Arrange
        WriteFile("Shelly.json", MetadataTestDevice.Serialize());
        WriteFile("Shelly.metadata.json", """{ "$subjects": { "Channels[0]": { "navTitle": "Hall light" } }, "$properties": { "Firmware": { "comment": "old" } } }""");
        var storage = await ConnectAsync();
        var device = Assert.IsType<MetadataTestDevice>(storage.Children["Shelly"]);
        WriteFile("Shelly.metadata.json", """{ "navTitle": "Shelly" }""");

        // Act
        await storage.ReconcileAsync(Named("Shelly.metadata.json"));

        // Assert
        Assert.Equal("Shelly", device.GetMetadata().GetString(MetadataKeys.NavigationTitle));
        Assert.Null(device.Channels[0].GetMetadata());
        Assert.Null(GetProperty(device, nameof(MetadataTestDevice.Firmware)).GetMetadata());
    }

    [Fact]
    public async Task WhenMetadataOfANestedSubjectAndOfItsPropertyIsSet_ThenTheSidecarHasTheirEntriesAndTheRegistryHasTheValues()
    {
        // Arrange
        WriteFile("Shelly.json", MetadataTestDevice.Serialize());
        var storage = await ConnectAsync();
        var device = Assert.IsType<MetadataTestDevice>(storage.Children["Shelly"]);

        // Act
        await storage.SetMetadataAsync(device.Channels[0], null, Values("""{ "navTitle": "Hall light" }"""), CancellationToken.None);
        await storage.SetMetadataAsync(device.Channels[0], nameof(MetadataTestChannel.Power), Values("""{ "comment": "includes the hall heater" }"""), CancellationToken.None);
        await storage.SetMetadataAsync(device, nameof(MetadataTestDevice.Firmware), Values("""{ "comment": "auto-update disabled" }"""), CancellationToken.None);
        await storage.SetMetadataAsync(device.Meters["Main"], null, Values("""{ "area": "Utility room" }"""), CancellationToken.None);

        // Assert
        Assert.Equal(
            """
            {
              "$subjects": {
                "Channels[0]": {
                  "navTitle": "Hall light"
                },
                "Meters[Main]": {
                  "area": "Utility room"
                }
              },
              "$properties": {
                "Channels[0]/Power": {
                  "comment": "includes the hall heater"
                },
                "Firmware": {
                  "comment": "auto-update disabled"
                }
              }
            }

            """.ReplaceLineEndings("\n"),
            ReadText("Shelly.metadata.json"));
        Assert.Equal("Hall light", device.Channels[0].GetMetadata().GetString(MetadataKeys.NavigationTitle));
        Assert.Equal("includes the hall heater", GetProperty(device.Channels[0], nameof(MetadataTestChannel.Power)).GetMetadata().GetString("comment"));
        Assert.Null(device.GetMetadata());
    }

    [Fact]
    public async Task WhenAnEntryWasWrittenInTheRouteNotation_ThenAWriteMergesItAndWritesThePathOfTheRegistry()
    {
        // Arrange
        WriteFile("Shelly.json", MetadataTestDevice.Serialize());
        WriteFile("Shelly.metadata.json", """{ "$subjects": { "Channels/1": { "navTitle": "Hall light", "area": "Hall" } } }""");
        var storage = await ConnectAsync();
        var device = Assert.IsType<MetadataTestDevice>(storage.Children["Shelly"]);

        // Act
        await storage.SetMetadataAsync(device.Channels[1], null, Values("""{ "navTitle": "Porch light" }"""), CancellationToken.None);

        // Assert
        Assert.Equal(
            "{\n  \"$subjects\": {\n    \"Channels[1]\": {\n      \"navTitle\": \"Porch light\",\n      \"area\": \"Hall\"\n    }\n  }\n}\n",
            ReadText("Shelly.metadata.json"));
    }

    [Fact]
    public async Task WhenAnEntryHasNoTarget_ThenItStaysInTheFile()
    {
        // Arrange
        WriteFile("Shelly.json", MetadataTestDevice.Serialize());
        WriteFile("Shelly.metadata.json", """{ "$subjects": { "Channels[5]": { "navTitle": "Later" } } }""");
        var storage = await ConnectAsync();
        var device = Assert.IsType<MetadataTestDevice>(storage.Children["Shelly"]);

        // Act
        await storage.SetMetadataAsync(device, null, Values("""{ "navTitle": "Shelly" }"""), CancellationToken.None);

        // Assert
        Assert.Contains("\"Channels[5]\"", ReadText("Shelly.metadata.json"));
        Assert.All(device.Channels, channel => Assert.Null(channel.GetMetadata()));
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData(KnownAttributes.Metadata)]
    public async Task WhenThePropertyIsNoPropertyOfTheSubject_ThenTheWriteIsRefused(string propertyName)
    {
        // Arrange
        WriteFile("Shelly.json", MetadataTestDevice.Serialize());
        var storage = await ConnectAsync();
        var device = storage.Children["Shelly"];

        // Act & Assert
        var exception = await Assert.ThrowsAsync<StorageRefusedException>(() =>
            storage.SetMetadataAsync(device, propertyName, Values("""{ "comment": "x" }"""), CancellationToken.None));
        Assert.Contains($"'{propertyName}'", exception.Message);
        Assert.False(File.Exists(GetFullPath("Shelly.metadata.json")));
    }

    [Theory]
    [InlineData("A/B")]
    [InlineData("A\\B")]
    [InlineData("A[0]")]
    [InlineData("A'B")]
    public async Task WhenThePropertyNameHasACharacterOfAPath_ThenTheWriteIsRefused(string propertyName)
    {
        // Arrange
        WriteFile("Shelly.json", MetadataTestDevice.Serialize());
        var storage = await ConnectAsync();
        var device = storage.Children["Shelly"];
        device.TryGetRegisteredSubject()!.AddProperty(propertyName, typeof(int), _ => 1, null);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<StorageRefusedException>(() =>
            storage.SetMetadataAsync(device, propertyName, Values("""{ "comment": "x" }"""), CancellationToken.None));
        Assert.Contains("cannot have metadata", exception.Message);
        Assert.False(File.Exists(GetFullPath("Shelly.metadata.json")));
    }

    [Fact]
    public async Task WhenThePropertyOfAFolderIsGiven_ThenTheWriteIsRefused()
    {
        // Arrange
        WriteFile("Docs/a.md");
        var storage = await ConnectAsync();
        var docs = storage.Children["Docs"];

        // Act & Assert
        await Assert.ThrowsAsync<StorageRefusedException>(() =>
            storage.SetMetadataAsync(docs, nameof(VirtualFolder.Children), Values("""{ "comment": "x" }"""), CancellationToken.None));
    }

    [Fact]
    public async Task WhenOrderIsSetForAStoredSubject_ThenItIsRefused()
    {
        // Arrange
        WriteFile("Shelly.json", MetadataTestDevice.Serialize());
        var storage = await ConnectAsync();

        // Act & Assert
        var exception = await Assert.ThrowsAsync<StorageRefusedException>(() =>
            storage.SetMetadataAsync(storage.Children["Shelly"], null, Values("""{ "order": ["a"] }"""), CancellationToken.None));
        Assert.Contains("only a folder or the root", exception.Message);
    }

    [Fact]
    public async Task WhenMetadataIsWritten_ThenNoOneElseCanSetItsValue()
    {
        // Arrange
        WriteFile("Shelly.json", MetadataTestDevice.Serialize());
        var storage = await ConnectAsync();
        var device = storage.Children["Shelly"];

        // Act
        await storage.SetMetadataAsync(device, null, Values("""{ "navTitle": "Shelly" }"""), CancellationToken.None);
        await storage.SetMetadataAsync(device, nameof(MetadataTestDevice.Firmware), Values("""{ "comment": "x" }"""), CancellationToken.None);

        // Assert
        Assert.False(GetProperty(device, KnownAttributes.Metadata).HasSetter);
        Assert.False(GetProperty(device, $"{nameof(MetadataTestDevice.Firmware)}@{KnownAttributes.Metadata}").HasSetter);
    }

    [Fact]
    public async Task WhenAStoreIsAskedWhatItKeeps_ThenItKeepsItselfItsFoldersItsJsonSubjectsAndWhatIsInsideThem()
    {
        // Arrange
        WriteFile("Docs/Readme.md");
        WriteFile("Shelly.json", MetadataTestDevice.Serialize());
        var storage = await ConnectAsync();
        var device = Assert.IsType<MetadataTestDevice>(storage.Children["Shelly"]);
        var docs = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);

        // Act & Assert
        Assert.True(storage.CanStoreMetadata(storage));
        Assert.True(storage.CanStoreMetadata(docs));
        Assert.True(storage.CanStoreMetadata(device));
        Assert.True(storage.CanStoreMetadata(device.Channels[1]));
        Assert.False(storage.CanStoreMetadata(docs.Children["Readme.md"]));
        Assert.Same(storage, device.Channels[1].FindMetadataStore());
    }

    [Theory]
    [InlineData("set", LoneSurrogateContent, "Pump")]
    [InlineData("order", LoneSurrogateContent, "Pump")]
    [InlineData("set", "not json", null)]
    [InlineData("order", "not json", null)]
    public async Task WhenTheFileCannotBeReadOrWrittenBack_ThenAWriteIsRefusedNamingTheFile(
        string call, string content, string? loadedNavigationTitle)
    {
        // Arrange
        WriteFile("a.md");
        WriteFile("b.md");
        WriteFile("Motor.json", SerializeMotor("Motor 1"));
        WriteFile("Motor.metadata.json", content);
        WriteFile(".metadata.json", content);
        var storage = await ConnectAsync();
        var motor = storage.Children["Motor"];
        Func<Task> act = call == "set"
            ? () => storage.SetMetadataAsync(motor, null, Values("""{ "icon": "Water" }"""), CancellationToken.None)
            : () => MetadataStoreExtensions.SetOrderAsync(storage, ["b.md"], CancellationToken.None);
        var path = call == "order" ? ".metadata.json" : "Motor.metadata.json";

        // Act
        var exception = await Assert.ThrowsAsync<StorageRefusedException>(act);

        // Assert
        Assert.Contains($"'{path}' could not be read", exception.Message);
        Assert.Equal(loadedNavigationTitle, motor.GetMetadata().GetString(MetadataKeys.NavigationTitle));
        Assert.Equal(content, File.ReadAllText(GetFullPath(path)));
    }

    [Fact]
    public async Task WhenSubjectIsReplacedByAnotherType_ThenTheNewSubjectHasTheMetadataBesideItsFileAndTheOldOneIsRefused()
    {
        // Arrange
        WriteFile("Motor.json", SerializeMotor("Motor 1"));
        WriteFile("Motor.metadata.json", """{ "navTitle": "Pump" }""");
        var storage = await ConnectAsync();
        var oldMotor = storage.Children["Motor"];
        WriteFile("Motor.json", SerializeStorage(CreateTemporaryDirectory().FullName));

        // Act
        await storage.ReconcileAsync(Named("Motor.json"));

        // Assert
        var replaced = Assert.IsType<FluentStorageContainer>(storage.Children["Motor"]);
        Assert.Equal("Pump", replaced.GetMetadata().GetString(MetadataKeys.NavigationTitle));
        await Assert.ThrowsAsync<StorageRefusedException>(() =>
            storage.SetMetadataAsync(oldMotor, null, Values("""{ "navTitle": "Old" }"""), CancellationToken.None));
        Assert.Equal("Pump", ReadMetadataFile("Motor.metadata.json").Own.GetString(MetadataKeys.NavigationTitle));
    }

    [Fact]
    public async Task WhenAFolderIsDeletedAndCreatedAgain_ThenTheDeletedFolderWritesNothing()
    {
        // Arrange
        WriteFile("Docs/a.md");
        var storage = await ConnectAsync();
        var deletedFolder = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);
        Directory.Delete(GetFullPath("Docs"), recursive: true);
        await storage.ReconcileAsync();
        WriteFile("Docs/b.md");
        await storage.ReconcileAsync();

        // Act
        var exception = await Assert.ThrowsAsync<StorageRefusedException>(() =>
            storage.SetMetadataAsync(deletedFolder, null, Values("""{ "title": "Old" }"""), CancellationToken.None));

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
            // Act
            await nested.FindMetadataStore()!.SetMetadataAsync(nested, null, Values("""{ "navTitle": "Archive" }"""), CancellationToken.None);
            await MetadataStoreExtensions.SetOrderAsync(nested, ["b.md"], CancellationToken.None);

            // Assert
            Assert.Same(storage, nested.FindMetadataStore());
            Assert.Equal("Archive", nested.GetMetadata().GetString(MetadataKeys.NavigationTitle));
            Assert.Equal("Archive", ReadMetadataFile("Nested.metadata.json").Own.GetString(MetadataKeys.NavigationTitle));
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
    public async Task WhenAStorageThatAnotherOneHoldsWritesItsOwnMetadata_ThenOnlyItsOrderIsAccepted()
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
            // Act
            var exception = await Assert.ThrowsAsync<StorageRefusedException>(() =>
                nested.SetMetadataAsync(nested, null, Values("""{ "navTitle": "Archive", "order": ["a.md"] }"""), CancellationToken.None));
            await nested.SetMetadataAsync(nested, null, Values("""{ "order": ["a.md"] }"""), CancellationToken.None);

            // Assert
            Assert.False(nested.CanStoreMetadata(nested));
            Assert.True(storage.CanStoreMetadata(nested));
            Assert.Contains("only its order", exception.Message);
            Assert.Null(nested.GetMetadata());
            Assert.Equal(["a.md"], nested.ChildOrder.ToArray());
            Assert.DoesNotContain("navTitle", File.ReadAllText(Path.Combine(nestedDirectory.FullName, ".metadata.json")));
        }
        finally
        {
            nested.Dispose();
        }
    }

    [Fact]
    public async Task WhenAStorageThatAnotherOneHoldsWritesItsOrderInAnotherCase_ThenItIsAccepted()
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
            // Act
            await nested.SetMetadataAsync(nested, null, Values("""{ "Order": ["a.md"] }"""), CancellationToken.None);

            // Assert
            Assert.Equal(["a.md"], nested.ChildOrder.ToArray());
        }
        finally
        {
            nested.Dispose();
        }
    }

    [Fact]
    public async Task WhenANestedStorageIsRemovedFromTheOuterStorage_ThenItsMetadataIsNoLongerWritten()
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
            File.Delete(GetFullPath("Nested.json"));
            await storage.ReconcileAsync(Named("Nested.json"));

            // Act
            var exception = await Assert.ThrowsAsync<StorageRefusedException>(() =>
                storage.SetMetadataAsync(nested, null, Values("""{ "navTitle": "Archive" }"""), CancellationToken.None));

            // Assert
            Assert.False(storage.Children.ContainsKey("Nested"));
            Assert.Contains("Only a storage", exception.Message);
            Assert.False(File.Exists(GetFullPath("Nested.metadata.json")));
        }
        finally
        {
            nested.Dispose();
        }
    }

    [Fact]
    public async Task WhenHistoryAndTheOpcUaServerLookAtAStoredSubject_ThenTheySkipItsMetadata()
    {
        // Arrange
        WriteFile("Motor.json", SerializeMotor("Motor 1"));
        WriteFile("Motor.metadata.json", """{ "navTitle": "Pump", "$properties": { "CurrentSpeed": { "comment": "x" } } }""");
        var storage = await ConnectAsync();
        var motor = storage.Children["Motor"];
        var metadata = GetProperty(motor, KnownAttributes.Metadata);
        var attribute = GetProperty(motor, $"{nameof(Samples.Motor.CurrentSpeed)}@{KnownAttributes.Metadata}");
        var provider = new StateAttributeOpcUaPathProvider();

        // Act & Assert
        Assert.False(metadata.HasHistory());
        Assert.False(attribute.HasHistory());
        Assert.False(provider.IsPropertyIncluded(metadata));
        Assert.False(provider.IsPropertyIncluded(attribute));
        Assert.True(provider.IsPropertyIncluded(GetProperty(motor, nameof(Samples.Motor.CurrentSpeed))));
    }
}
