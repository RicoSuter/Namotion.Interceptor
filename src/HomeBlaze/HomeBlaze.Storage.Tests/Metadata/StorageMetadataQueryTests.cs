using System.Collections.Immutable;
using System.Text.Json;
using HomeBlaze.Authorization.Abstractions;
using HomeBlaze.Services.Metadata;
using HomeBlaze.Storage.Abstractions.Metadata;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Storage.Tests.Metadata;

public class StorageMetadataQueryTests : StorageTestBase
{
    private static JsonElement Values(string json) => JsonElement.Parse(json);

    private async Task<FluentStorageContainer> ConnectWithTaggedDeviceAndChannelAsync()
    {
        WriteFile("Shelly.json", MetadataTestDevice.Serialize());
        var storage = await ConnectAsync();
        await storage.SetMetadataAsync("Shelly", null, Values("""{ "tags": ["floor-2"] }"""), CancellationToken.None);
        await storage.SetMetadataAsync("Shelly/Channels[1]", null, Values("""{ "tags": ["floor-2"] }"""), CancellationToken.None);
        return storage;
    }

    [Fact]
    public async Task WhenThePermissionsDoNotReadTheMetadataOfASubject_ThenSearchMetadataLeavesItOut()
    {
        // Arrange
        var storage = await ConnectWithTaggedDeviceAndChannelAsync();
        var permissions = new QueryPermissions(canRead: member => !(member.IsMetadata() && member.Parent.Subject is MetadataTestChannel));

        // Act
        var matches = storage.SearchMetadata("floor-2", null, null, null, permissions);

        // Assert
        Assert.Equal(["Shelly"], matches.Select(match => match.Path));
    }

    [Fact]
    public async Task WhenThePermissionsDoNotShowASubject_ThenSearchMetadataLeavesItOut()
    {
        // Arrange
        var storage = await ConnectWithTaggedDeviceAndChannelAsync();
        var permissions = new QueryPermissions(canShow: subject => subject.Subject is not MetadataTestChannel);

        // Act
        var matches = storage.SearchMetadata("floor-2", null, null, null, permissions);

        // Assert
        Assert.Equal(["Shelly"], matches.Select(match => match.Path));
    }

    [Fact]
    public async Task WhenThePermissionsShowMetadataWithoutTheTag_ThenSearchMetadataLeavesItOut()
    {
        // Arrange
        var storage = await ConnectWithTaggedDeviceAndChannelAsync();
        var permissions = new QueryPermissions(getValue: member => member.Parent.Subject is MetadataTestDevice && member.IsMetadata()
            ? ImmutableDictionary<string, JsonElement>.Empty
            : member.GetValue());

        // Act
        var matches = storage.SearchMetadata("floor-2", null, null, null, permissions);

        // Assert
        Assert.Equal(["Shelly/Channels[1]"], matches.Select(match => match.Path));
    }

    [Fact]
    public async Task WhenTheFirstMemberThatHoldsASubjectIsNotReadable_ThenSearchMetadataFindsItThroughAReadableOne()
    {
        // Arrange
        WriteFile("Shelly.json", MetadataTestDevice.Serialize());
        var storage = await ConnectAsync();
        var device = (MetadataTestDevice)storage.Children["Shelly"];
        device.Meters = new Dictionary<string, MetadataTestChannel> { ["Main"] = device.Channel! };
        await storage.SetMetadataAsync("Shelly/Meters[Main]", null, Values("""{ "tags": ["floor-2"] }"""), CancellationToken.None);
        var permissions = new QueryPermissions(canRead: member => !(member.Name == nameof(MetadataTestDevice.Channel) && member.Parent.Subject is MetadataTestDevice));

        // Act
        var matches = storage.SearchMetadata("floor-2", null, null, null, permissions);

        // Assert
        Assert.Equal(["Shelly/Meters[Main]"], matches.Select(match => match.Path));
    }

    [Fact]
    public async Task WhenNoReadableMemberHoldsASubject_ThenSearchMetadataLeavesItOut()
    {
        // Arrange
        WriteFile("Shelly.json", MetadataTestDevice.Serialize());
        var storage = await ConnectAsync();
        await storage.SetMetadataAsync("Shelly/Channel", null, Values("""{ "tags": ["floor-2"] }"""), CancellationToken.None);
        var permissions = new QueryPermissions(canRead: member => !(member.Name == nameof(MetadataTestDevice.Channel) && member.Parent.Subject is MetadataTestDevice));

        // Act
        var matches = storage.SearchMetadata("floor-2", null, null, null, permissions);

        // Assert
        Assert.Empty(matches);
    }

    [Fact]
    public async Task WhenASubjectOnThePathIsNotShown_ThenSearchMetadataLeavesOutTheSubjectsBelowIt()
    {
        // Arrange
        var storage = await ConnectWithTaggedDeviceAndChannelAsync();
        var permissions = new QueryPermissions(canShow: subject => subject.Subject is not MetadataTestDevice);

        // Act
        var matches = storage.SearchMetadata("floor-2", null, null, null, permissions);

        // Assert
        Assert.Empty(matches);
    }

    [Fact]
    public async Task WhenNoPermissionsAreGiven_ThenTheQueriesFindNothing()
    {
        // Arrange
        var storage = await ConnectWithTaggedDeviceAndChannelAsync();

        // Act
        var matches = storage.SearchMetadata("floor-2", null, null, null, permissions: null);
        var order = storage.GetOrder(permissions: null);

        // Assert
        Assert.Empty(matches);
        Assert.Empty(order);
    }

    [Fact]
    public async Task WhenThePermissionsDoNotShowAChild_ThenGetOrderLeavesItOut()
    {
        // Arrange
        WriteFile("Pump.json", MetadataTestDevice.Serialize());
        WriteFile("Shelly.json", MetadataTestDevice.Serialize());
        WriteFile("Docs/Readme.md", "# Readme");
        var storage = await ConnectAsync();
        var hidden = storage.Children["Shelly"];
        var permissions = new QueryPermissions(canShow: subject => !ReferenceEquals(subject.Subject, hidden));

        // Act
        var order = storage.GetOrder(permissions);

        // Assert
        Assert.Equal(["Docs", "Pump"], order);
    }

    [Fact]
    public async Task WhenThePermissionsDoNotShowAChildOfAFolder_ThenItsGetOrderLeavesItOut()
    {
        // Arrange
        WriteFile("Docs/Pump.json", MetadataTestDevice.Serialize());
        WriteFile("Docs/Shelly.json", MetadataTestDevice.Serialize());
        var storage = await ConnectAsync();
        var folder = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);
        var hidden = folder.Children["Shelly"];
        var permissions = new QueryPermissions(canShow: subject => !ReferenceEquals(subject.Subject, hidden));

        // Act
        var order = folder.GetOrder(permissions);

        // Assert
        Assert.Equal(["Pump"], order);
    }

    [Fact]
    public async Task WhenThePermissionsDoNotReadTheChildren_ThenGetOrderNamesNoChild()
    {
        // Arrange
        WriteFile("Pump.json", MetadataTestDevice.Serialize());
        var storage = await ConnectAsync();
        var permissions = new QueryPermissions(canRead: member => member.Name != IMetadataContainer.ChildrenPropertyName);

        // Act
        var order = storage.GetOrder(permissions);

        // Assert
        Assert.Empty(order);
    }

    // Reads and shows what the predicates allow, everything by default, and shows the values getValue returns.
    private sealed class QueryPermissions(
        Func<RegisteredSubjectProperty, bool>? canRead = null,
        Func<RegisteredSubject, bool>? canShow = null,
        Func<RegisteredSubjectProperty, object?>? getValue = null) : IUserPermissions
    {
        public event Action? Changed
        {
            add { }
            remove { }
        }

        public bool CanRead(RegisteredSubjectProperty member) => canRead?.Invoke(member) ?? true;

        public object? GetValue(RegisteredSubjectProperty member) => getValue is null ? member.GetValue() : getValue(member);

        public bool CanWrite(RegisteredSubjectProperty member) => false;

        public bool CanInvoke(RegisteredSubjectProperty member) => false;

        public bool CanAdminister(RegisteredSubjectProperty member) => false;

        public bool CanReadHistory(RegisteredSubjectProperty member) => false;

        public bool CanShow(RegisteredSubject subject) => canShow?.Invoke(subject) ?? true;

        public bool HoldsAllRoles(IReadOnlyCollection<string> roles) => false;

        public bool CanActThrough(IReadOnlyCollection<string> roles) => false;

        public IReadOnlyList<string> GetRolesWithDenyRulesMissingFrom(IReadOnlyCollection<string> roles) => [];
    }
}
