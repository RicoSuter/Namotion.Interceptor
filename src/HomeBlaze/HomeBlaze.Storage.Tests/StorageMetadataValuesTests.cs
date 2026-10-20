using System.Collections.Immutable;
using System.Text.Json;
using HomeBlaze.Abstractions;
using HomeBlaze.Services;
using HomeBlaze.Storage.Internal;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace HomeBlaze.Storage.Tests;

public class StorageMetadataValuesTests
{
    private static ImmutableDictionary<string, JsonElement> Parse(string json)
        => JsonElement.Parse(json).EnumerateObject().ToImmutableDictionary(property => property.Name, property => property.Value);

    [Fact]
    public void WhenTheSubjectAttachedAgainAfterItsRegistrationWasRead_ThenItsValuesStillChange()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var channel = new MetadataTestChannel();
        var device = new MetadataTestDevice(context) { Channel = channel };
        var staleRegistration = channel.TryGetRegisteredSubject()!;
        device.Channel = null;
        device.Channel = channel;
        var first = Parse("""{ "navTitle": "Pump" }""");
        var second = Parse("""{ "navTitle": "Fan" }""");

        // Act
        StorageMetadataValues.Set(staleRegistration, null, first);
        StorageMetadataValues.Set(staleRegistration, nameof(MetadataTestChannel.Power), first);
        var registration = channel.TryGetRegisteredSubject()!;
        StorageMetadataValues.Set(registration, null, second);
        StorageMetadataValues.Set(registration, nameof(MetadataTestChannel.Power), second);

        // Assert
        Assert.NotSame(staleRegistration, registration);
        Assert.Same(second, channel.GetMetadata());
        Assert.Same(second, registration.TryGetProperty(nameof(MetadataTestChannel.Power))!.GetMetadata());
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
