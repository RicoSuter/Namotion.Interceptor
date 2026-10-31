using System.Collections.Immutable;
using System.Text.Json;
using HomeBlaze.Abstractions;
using HomeBlaze.Services.Metadata;
using HomeBlaze.Storage.Metadata;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

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

    [Fact]
    public void WhenAFirstValueAddsTheMetadata_ThenItIsAddedUnderTheLockOfTheSubject()
    {
        // Arrange
        var handler = new LockRecordingHandler();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry()
            .WithService<IPropertyLifecycleHandler>(() => handler, existing => ReferenceEquals(existing, handler));
        var channel = new MetadataTestChannel();
        _ = new MetadataTestDevice(context) { Channel = channel };
        var registration = channel.TryGetRegisteredSubject()!;

        // Act
        StorageMetadataValues.Set(registration, null, Parse("""{ "navTitle": "Pump" }"""));
        StorageMetadataValues.Set(registration, nameof(MetadataTestChannel.Power), Parse("""{ "comment": "outside" }"""));

        // Assert
        Assert.Equal([true, true], handler.AddedUnderLock);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(nameof(MetadataTestChannel.Power))]
    public void WhenTheMetadataMemberIsMissing_ThenItIsAddedWithoutMetadataAndALaterValueIsSetInIt(string? propertyName)
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var channel = new MetadataTestChannel();
        _ = new MetadataTestDevice(context) { Channel = channel };
        var registration = channel.TryGetRegisteredSubject()!;
        var values = Parse("""{ "navTitle": "Pump" }""");

        // Act
        var member = StorageMetadataValues.GetOrAddMember(registration, propertyName);
        var valueBefore = member?.GetValue();
        StorageMetadataValues.Set(registration, propertyName, values);

        // Assert
        Assert.NotNull(member);
        Assert.True(member.IsMetadata());
        Assert.Null(valueBefore);
        Assert.Same(member, StorageMetadataValues.GetOrAddMember(registration, propertyName));
        Assert.Same(values, member.GetValue());
    }

    [Fact]
    public void WhenTheMetadataMemberExists_ThenItIsReturnedWithItsValue()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var channel = new MetadataTestChannel();
        _ = new MetadataTestDevice(context) { Channel = channel };
        var registration = channel.TryGetRegisteredSubject()!;
        var values = Parse("""{ "navTitle": "Pump" }""");
        StorageMetadataValues.Set(registration, null, values);

        // Act
        var member = StorageMetadataValues.GetOrAddMember(registration, null);

        // Assert
        Assert.Same(registration.TryGetProperty(KnownAttributes.Metadata), member);
        Assert.Same(values, channel.GetMetadata());
    }

    [Theory]
    [InlineData("Missing")]
    [InlineData(KnownAttributes.Metadata)]
    [InlineData(nameof(MetadataTestChannel.Power) + "@" + KnownAttributes.Metadata)]
    public void WhenThePropertyCannotHaveMetadata_ThenNoMemberIsAdded(string propertyName)
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var channel = new MetadataTestChannel();
        _ = new MetadataTestDevice(context) { Channel = channel };
        var registration = channel.TryGetRegisteredSubject()!;
        StorageMetadataValues.GetOrAddMember(registration, null);
        StorageMetadataValues.GetOrAddMember(registration, nameof(MetadataTestChannel.Power));
        var propertyCount = registration.Properties.Length;

        // Act
        var member = StorageMetadataValues.GetOrAddMember(registration, propertyName);

        // Assert
        Assert.Null(member);
        Assert.Equal(propertyCount, registration.Properties.Length);
    }

    [Fact]
    public void WhenTheRegistrationIsStale_ThenNoMemberIsAdded()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var channel = new MetadataTestChannel();
        var device = new MetadataTestDevice(context) { Channel = channel };
        var staleRegistration = channel.TryGetRegisteredSubject()!;
        device.Channel = null;
        device.Channel = channel;

        // Act
        var member = StorageMetadataValues.GetOrAddMember(staleRegistration, null);
        StorageMetadataValues.Set(channel.TryGetRegisteredSubject()!, null, Parse("""{ "navTitle": "Pump" }"""));

        // Assert
        Assert.Null(member);
        Assert.Null(staleRegistration.TryGetProperty(KnownAttributes.Metadata));
        Assert.Equal("Pump", channel.GetMetadata()!["navTitle"].GetString());
    }

    private sealed class LockRecordingHandler : IPropertyLifecycleHandler
    {
        public List<bool> AddedUnderLock { get; } = [];

        public void AttachProperty(SubjectPropertyLifecycleChange change)
        {
            if (change.Property.Name == KnownAttributes.Metadata || change.Property.Name.EndsWith($"@{KnownAttributes.Metadata}", StringComparison.Ordinal))
            {
                AddedUnderLock.Add(Monitor.IsEntered(StorageMetadataValues.GetLock(change.Subject)));
            }
        }

        public void DetachProperty(SubjectPropertyLifecycleChange change)
        {
        }
    }
}
