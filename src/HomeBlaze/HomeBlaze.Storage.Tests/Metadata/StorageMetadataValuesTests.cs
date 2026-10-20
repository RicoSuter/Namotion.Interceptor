using System.Collections.Immutable;
using System.Text.Json;
using HomeBlaze.Abstractions;
using HomeBlaze.Services.Metadata;
using HomeBlaze.Storage.Metadata;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace HomeBlaze.Storage.Tests.Metadata;

public class StorageMetadataValuesTests
{
    private static ImmutableDictionary<string, JsonElement> Parse(string json)
        => JsonElement.Parse(json).EnumerateObject().ToImmutableDictionary(property => property.Name, property => property.Value);

    [Fact]
    public void WhenTheSubjectAttachedAgainAfterItsRegistrationWasRead_ThenTheStaleRegistrationSetsNothingAndTheCurrentOneSets()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var channel = new MetadataTestChannel();
        var device = new MetadataTestDevice(context) { Channel = channel };
        var staleRegistration = channel.TryGetRegisteredSubject()!;
        device.Channel = null;
        device.Channel = channel;
        var current = Parse("""{ "navTitle": "Fan" }""");
        var stale = Parse("""{ "navTitle": "Pump" }""");
        var registration = channel.TryGetRegisteredSubject()!;

        // Act
        StorageMetadataValues.Set(registration, null, current);
        StorageMetadataValues.Set(registration, nameof(MetadataTestChannel.Power), current);
        StorageMetadataValues.Set(staleRegistration, null, stale);
        StorageMetadataValues.Set(staleRegistration, nameof(MetadataTestChannel.Power), stale);

        // Assert
        Assert.NotSame(staleRegistration, registration);
        Assert.Same(current, channel.GetMetadata());
        Assert.Same(current, registration.TryGetProperty(nameof(MetadataTestChannel.Power))!.GetMetadata());
    }

    [Fact]
    public void WhenTheSubjectAttachedAgainBeforeAnyValueWasSet_ThenTheStaleRegistrationAddsNothing()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var channel = new MetadataTestChannel();
        var device = new MetadataTestDevice(context) { Channel = channel };
        var staleRegistration = channel.TryGetRegisteredSubject()!;
        device.Channel = null;
        device.Channel = channel;

        // Act
        StorageMetadataValues.Set(staleRegistration, null, Parse("""{ "navTitle": "Pump" }"""));

        // Assert
        Assert.Null(channel.GetMetadata());
        Assert.Null(channel.TryGetRegisteredSubject()!.TryGetProperty(KnownAttributes.Metadata));
    }

    [Fact]
    public void WhenTheSubjectIsNoLongerRegistered_ThenNothingIsSet()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var channel = new MetadataTestChannel();
        var device = new MetadataTestDevice(context) { Channel = channel };
        var staleRegistration = channel.TryGetRegisteredSubject()!;
        device.Channel = null;

        // Act
        StorageMetadataValues.Set(staleRegistration, null, Parse("""{ "navTitle": "Pump" }"""));

        // Assert
        Assert.Null(staleRegistration.TryGetProperty(KnownAttributes.Metadata));
    }
}
