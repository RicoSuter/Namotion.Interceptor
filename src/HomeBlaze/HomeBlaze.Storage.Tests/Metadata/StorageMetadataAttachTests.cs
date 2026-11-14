using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using HomeBlaze.Services.Metadata;
using HomeBlaze.Storage.Abstractions.Metadata;
using HomeBlaze.Storage.Metadata;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace HomeBlaze.Storage.Tests.Metadata;

public class StorageMetadataAttachTests : StorageTestBase
{
    private const string Sidecar = """
        {
          "$subjects": {
            "Channel": { "navTitle": "Pump relay" },
            "Channels[2]": { "navTitle": "Porch light" }
          },
          "$properties": {
            "Channels[2]/Power": { "comment": "outside" },
            "Channels[0]/Energy": { "comment": "added at run time" }
          }
        }
        """;

    private async Task<(FluentStorageContainer Storage, MetadataTestDevice Device)> LoadDeviceAsync()
    {
        WriteFile("Shelly.json", MetadataTestDevice.Serialize());
        WriteFile("Shelly.metadata.json", Sidecar);
        var storage = await ConnectAsync();
        return (storage, Assert.IsType<MetadataTestDevice>(storage.Children["Shelly"]));
    }

    [Fact]
    public async Task WhenASubjectIsAddedInsideAStoredSubject_ThenItAndItsPropertiesTakeTheirEntries()
    {
        // Arrange
        var (_, device) = await LoadDeviceAsync();
        var channel = new MetadataTestChannel();

        // Act
        device.Channels = [.. device.Channels, channel];

        // Assert
        Assert.Equal("Porch light", channel.GetMetadata().GetString(MetadataKeys.NavigationTitle));
        var power = channel.TryGetRegisteredSubject()!.TryGetProperty(nameof(MetadataTestChannel.Power))!;
        Assert.Equal("outside", power.GetMetadata().GetString("comment"));
    }

    [Fact]
    public async Task WhenASubjectInsideAStoredSubjectIsReplaced_ThenTheNewOneTakesTheEntry()
    {
        // Arrange
        var (_, device) = await LoadDeviceAsync();
        var channel = new MetadataTestChannel();

        // Act
        device.Channel = channel;

        // Assert
        Assert.Equal("Pump relay", channel.GetMetadata().GetString(MetadataKeys.NavigationTitle));
    }

    [Fact]
    public async Task WhenAPropertyIsAddedAtRunTime_ThenItTakesItsEntry()
    {
        // Arrange
        var (_, device) = await LoadDeviceAsync();
        var registered = device.Channels[0].TryGetRegisteredSubject()!;

        // Act
        var energy = registered.AddProperty("Energy", typeof(double), _ => 1.5, null);

        // Assert
        Assert.Equal("added at run time", energy.GetMetadata().GetString("comment"));
    }

    [Fact]
    public async Task WhenASubjectIsDetachedAndAttachedAgain_ThenItsMetadataStillChanges()
    {
        // Arrange
        var (storage, device) = await LoadDeviceAsync();
        var channel = device.Channel!;
        device.Channel = null;
        device.Channel = channel;

        // Act
        await storage.SetMetadataAsync(channel, null, JsonElement.Parse("""{ "navTitle": "Pump" }"""), CancellationToken.None);

        // Assert
        Assert.Equal("Pump", channel.GetMetadata().GetString(MetadataKeys.NavigationTitle));
    }

    [Fact]
    public async Task WhenASubjectMovesIntoAnotherStoredSubject_ThenItTakesTheEntryThere()
    {
        // Arrange
        WriteFile("Other.json", MetadataTestDevice.Serialize());
        WriteFile("Other.metadata.json", """{ "$subjects": { "Channel": { "navTitle": "Fan relay" } } }""");
        var (storage, device) = await LoadDeviceAsync();
        var other = Assert.IsType<MetadataTestDevice>(storage.Children["Other"]);
        var channel = device.Channel!;

        // Act
        device.Channel = null;
        other.Channel = channel;

        // Assert
        Assert.Equal("Fan relay", channel.GetMetadata().GetString(MetadataKeys.NavigationTitle));
    }

    [Fact]
    public async Task WhenASubjectMovesIntoAStoredSubjectWhoseFileHasNoEntries_ThenItLosesItsOldMetadata()
    {
        // Arrange
        WriteFile("Other.json", MetadataTestDevice.Serialize());
        var (storage, device) = await LoadDeviceAsync();
        var other = Assert.IsType<MetadataTestDevice>(storage.Children["Other"]);
        var channel = device.Channel!;

        // Act
        device.Channel = null;
        other.Channel = channel;

        // Assert
        Assert.Null(channel.GetMetadata().GetString(MetadataKeys.NavigationTitle));
    }

    [Fact]
    public void WhenAStoredSubjectIsNoLongerUsed_ThenTheAppliedMetadataDoesNotKeepIt()
    {
        // Arrange
        var applier = new StorageMetadataApplier(CreateStorage());
        var file = StorageMetadataFile.TryParse(Encoding.UTF8.GetBytes(Sidecar), out var parsed, out var error) ? parsed : throw error;

        // Act
        var reference = ApplyToNewDevice(applier, file);
        for (var attempt = 0; attempt < 10 && reference.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        // Assert
        Assert.False(reference.IsAlive);
    }

    // Not inlined, so that no local of the test keeps the device.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ApplyToNewDevice(StorageMetadataApplier applier, StorageMetadataFile file)
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var device = new MetadataTestDevice(context) { Channels = [new MetadataTestChannel(), new MetadataTestChannel(), new MetadataTestChannel()] };
        applier.Apply(device, file);
        Assert.Equal("Porch light", device.Channels[2].GetMetadata().GetString(MetadataKeys.NavigationTitle));
        return new WeakReference(device);
    }
}
