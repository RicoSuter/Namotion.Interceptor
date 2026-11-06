using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MQTTnet;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Mapping;
using Namotion.Interceptor.Mqtt.Client;
using Namotion.Interceptor.Mqtt.Mapping;
using Namotion.Interceptor.Mqtt.Server;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Attributes;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Mqtt.Tests;

/// <summary>
/// A connector's own root subject is anchored to the context rather than held by a parent property,
/// so nothing increments its reference count and it permanently reports zero. These pin that its
/// properties still reach the client's caches and the server's path cache, that a subject which has
/// left the graph does not, and that a lookup landing in the middle of a detach evicts its own entry.
/// They also pin that the server's change filter and publish follow the current mapping.
/// </summary>
public class MqttMappingCacheTests
{
    [Fact]
    public async Task WhenTheServerLooksUpTheTopicOfAPropertyTwice_ThenTheSecondLookupIsServedFromTheCache()
    {
        // Arrange
        var mapper = new CountingMapper(CreateMapper());
        var subject = CreateRootSubject();
        await using var server = CreateServer(subject, mapper);
        var property = subject.TryGetRegisteredSubject()!.TryGetProperty(nameof(MqttCacheTestRoot.Name))!;

        // Act
        var first = server.TryGetTopicForProperty(property);
        var second = server.TryGetTopicForProperty(property);

        // Assert
        Assert.Equal("name", first.Topic);
        Assert.Equal("name", second.Topic);
        Assert.Equal(1, mapper.MappingLookupCount);
    }

    [Fact]
    public async Task WhenRootSubjectPathIsResolvedTwice_ThenTheServerPathCacheServesTheSecondLookup()
    {
        // Arrange
        var mapper = new CountingMapper(CreateMapper());
        var subject = CreateRootSubject();
        await using var server = CreateServer(subject, mapper);

        // Act
        var first = await server.TryGetPropertyForTopicAsync("name", CancellationToken.None);
        var second = await server.TryGetPropertyForTopicAsync("name", CancellationToken.None);

        // Assert
        Assert.Equal(nameof(MqttCacheTestRoot.Name), first?.Name);
        Assert.Equal(nameof(MqttCacheTestRoot.Name), second?.Name);
        Assert.Equal(1, mapper.PropertyLookupCount);
    }

    [Fact]
    public async Task WhenRootSubjectPropertyIsMappedTwice_ThenTheClientTopicCacheServesTheSecondLookup()
    {
        // Arrange
        var mapper = new CountingMapper(CreateMapper());
        var subject = CreateRootSubject();
        await using var client = CreateClient(subject, mapper);
        var property = subject.TryGetRegisteredSubject()!.TryGetProperty(nameof(MqttCacheTestRoot.Name))!;

        // Act
        var first = client.TryGetTopicForProperty(property.Reference, property);
        var second = client.TryGetTopicForProperty(property.Reference, property);

        // Assert
        Assert.Equal("name", first.Topic);
        Assert.Equal("name", second.Topic);
        Assert.Equal(1, mapper.MappingLookupCount);
    }

    [Fact]
    public async Task WhenRootSubjectTopicIsResolvedTwice_ThenTheClientTopicCacheServesTheSecondLookup()
    {
        // Arrange
        var mapper = new CountingMapper(CreateMapper());
        var subject = CreateRootSubject();
        await using var client = CreateClient(subject, mapper);

        // Act
        var first = await client.TryGetPropertyForTopicAsync("name");
        var second = await client.TryGetPropertyForTopicAsync("name");

        // Assert
        Assert.Equal(nameof(MqttCacheTestRoot.Name), first?.Name);
        Assert.Equal(nameof(MqttCacheTestRoot.Name), second?.Name);
        Assert.Equal(1, mapper.PropertyLookupCount);
    }

    [Fact]
    public async Task WhenSubjectHasLeftTheRegistry_ThenTheClientTopicCacheDoesNotRetainIt()
    {
        // Arrange
        var mapper = new CountingMapper(CreateMapper());
        var subject = CreateRootSubject();
        await using var client = CreateClient(subject, mapper);
        var detachedProperty = DetachChildAndReturnStaleProperty(subject);

        // Act
        client.TryGetTopicForProperty(detachedProperty.Reference, detachedProperty);
        var retained = client.HasCachedTopic(detachedProperty.Reference);
        client.TryGetTopicForProperty(detachedProperty.Reference, detachedProperty);

        // Assert
        Assert.False(retained);
        Assert.Equal(0, mapper.MappingLookupCount);
    }

    [Fact]
    public async Task WhenTheConnectorRootHasLeftTheContext_ThenTheClientTopicCacheDoesNotRetainIt()
    {
        // Arrange
        var mapper = new CountingMapper(CreateMapper());
        var subject = CreateRootSubject();
        await using var client = CreateClient(subject, mapper);
        var property = subject.TryGetRegisteredSubject()!.TryGetProperty(nameof(MqttCacheTestRoot.Name))!;
        ((IInterceptorSubject)subject).Context.TryGetLifecycleInterceptor()!.DetachSubjectFromContext(subject);

        // Act
        client.TryGetTopicForProperty(property.Reference, property);
        var retained = client.HasCachedTopic(property.Reference);
        client.TryGetTopicForProperty(property.Reference, property);

        // Assert
        Assert.False(retained);
        Assert.Equal(0, mapper.MappingLookupCount);
    }

    [Fact]
    public async Task WhenLookupInsertsWhileSubjectIsDetaching_ThenTheClientTopicCacheDoesNotRetainIt()
    {
        // Arrange
        var mapper = new CountingMapper(CreateMapper());
        var subject = CreateRootSubject();
        await using var client = CreateClient(subject, mapper);

        // Act
        var (property, topicSeenDuringDetach) = DetachChildWhileInserting(
            subject, p => client.TryGetTopicForProperty(p.Reference, p).Topic);
        var retained = client.HasCachedTopic(property.Reference);
        var after = client.TryGetTopicForProperty(property.Reference, property);

        // Assert
        Assert.Equal("child/value", topicSeenDuringDetach);
        Assert.False(retained);
        Assert.Null(after.Topic);
        Assert.Equal(1, mapper.MappingLookupCount);
    }

    [Fact]
    public async Task WhenASubjectIsDetached_ThenTheClientCachesHoldNoEntryOfIt()
    {
        // Arrange
        var subject = CreateRootSubject();
        await using var client = CreateClient(subject, CreateMapper());
        subject.Child = new MqttCacheTestChild();
        var property = subject.Child.TryGetRegisteredSubject()!.TryGetProperty(nameof(MqttCacheTestChild.Value))!;
        client.TryGetTopicForProperty(property.Reference, property);
        await client.TryGetPropertyForTopicAsync("child/value");
        var cachedBeforeDetach = client.HasCachedTopic(property.Reference) && client.HasCachedProperty("child/value");

        // Act
        subject.Child = null;

        // Assert
        Assert.True(cachedBeforeDetach);
        Assert.False(client.HasCachedTopic(property.Reference));
        Assert.False(client.HasCachedProperty("child/value"));
    }

    [Fact]
    public async Task WhenASubjectIsDetached_ThenTheServerCachesHoldNoEntryOfIt()
    {
        // Arrange: the server is never started, so the test subscribes its detach handler the way RunAsync does.
        var subject = CreateRootSubject();
        await using var server = CreateServer(subject, CreateMapper());
        subject.Child = new MqttCacheTestChild();
        var property = subject.Child.TryGetRegisteredSubject()!.TryGetProperty(nameof(MqttCacheTestChild.Value))!;
        server.TryGetTopicForProperty(property);
        await server.TryGetPropertyForTopicAsync("child/value", CancellationToken.None);
        var cachedBeforeDetach = server.HasCachedTopic(property.Reference) && server.HasCachedPath("child/value");
        var lifecycle = ((IInterceptorSubject)subject).Context.TryGetLifecycleInterceptor()!;

        // Act
        lifecycle.SubjectDetaching += server.OnSubjectDetaching;
        try
        {
            subject.Child = null;
        }
        finally
        {
            lifecycle.SubjectDetaching -= server.OnSubjectDetaching;
        }

        // Assert
        Assert.True(cachedBeforeDetach);
        Assert.False(server.HasCachedTopic(property.Reference));
        Assert.False(server.HasCachedPath("child/value"));
    }

    [Fact]
    public async Task WhenLookupInsertsWhileSubjectIsDetaching_ThenTheClientPropertyCacheDoesNotRetainIt()
    {
        // Arrange
        var mapper = new CountingMapper(CreateMapper());
        var subject = CreateRootSubject();
        await using var client = CreateClient(subject, mapper);

        // Act
        var (_, resolvedDuringDetach) = DetachChildWhileInserting(subject, property =>
        {
            mapper.ResolvedPropertyOverride = property;
            return SyncResult(client.TryGetPropertyForTopicAsync("child/value"))?.Name;
        });
        mapper.ResolvedPropertyOverride = null;
        var retained = client.HasCachedProperty("child/value");
        var after = await client.TryGetPropertyForTopicAsync("child/value");

        // Assert
        Assert.Equal(nameof(MqttCacheTestChild.Value), resolvedDuringDetach);
        Assert.False(retained);
        Assert.Null(after);
        Assert.Equal(2, mapper.PropertyLookupCount);
    }

    [Fact]
    public async Task WhenLookupInsertsWhileSubjectIsDetaching_ThenTheServerPathCacheDoesNotRetainIt()
    {
        // Arrange: the server is never started, so its own detach cleanup is not subscribed and
        // the insert-time guard is the only thing that can drop the entry again.
        var mapper = new CountingMapper(CreateMapper());
        var subject = CreateRootSubject();
        await using var server = CreateServer(subject, mapper);

        // Act
        var (_, resolvedDuringDetach) = DetachChildWhileInserting(subject, property =>
        {
            mapper.ResolvedPropertyOverride = property;
            return SyncResult(server.TryGetPropertyForTopicAsync("child/value", CancellationToken.None))?.Name;
        });
        mapper.ResolvedPropertyOverride = null;
        var retained = server.HasCachedPath("child/value");
        var after = await server.TryGetPropertyForTopicAsync("child/value", CancellationToken.None);

        // Assert
        Assert.Equal(nameof(MqttCacheTestChild.Value), resolvedDuringDetach);
        Assert.False(retained);
        Assert.Null(after);
    }

    [Fact]
    public async Task WhenLookupInsertsWhileSubjectIsDetaching_ThenTheServerTopicCacheDoesNotRetainIt()
    {
        // Arrange: the server is never started, so its own detach cleanup is not subscribed and
        // the insert-time guard is the only thing that can drop the entry again.
        var subject = CreateRootSubject();
        await using var server = CreateServer(subject, CreateMapper());

        // Act
        var (property, topicSeenDuringDetach) = DetachChildWhileInserting(
            subject, p => server.TryGetTopicForProperty(p).Topic);

        // Assert
        Assert.Equal("child/value", topicSeenDuringDetach);
        Assert.False(server.HasCachedTopic(property.Reference));
    }

    [Fact]
    public async Task WhenSubjectHasLeftTheRegistry_ThenTheServerTopicCacheDoesNotRetainIt()
    {
        // Arrange
        var subject = CreateRootSubject();
        await using var server = CreateServer(subject, CreateMapper());
        var detachedProperty = DetachChildAndReturnStaleProperty(subject);

        // Act
        server.TryGetTopicForProperty(detachedProperty);

        // Assert
        Assert.False(server.HasCachedTopic(detachedProperty.Reference));
    }

    [Fact]
    public async Task WhenTheStructureChangesWhileTheServerResolvesATopic_ThenTheNextLookupResolvesItAgain()
    {
        // Arrange
        var mapper = new CountingMapper(CreateMapper());
        var subject = CreateRootSubject();
        var child = new MqttCacheTestChild();
        subject.Child = child;
        await using var server = CreateServer(subject, mapper);
        mapper.DuringNextPropertyLookup = () =>
        {
            subject.Other = child;
            subject.Child = null;
        };

        // Act
        var racing = await server.TryGetPropertyForTopicAsync("child/value", CancellationToken.None);
        var next = await server.TryGetPropertyForTopicAsync("child/value", CancellationToken.None);

        // Assert
        Assert.Equal(nameof(MqttCacheTestChild.Value), racing?.Name);
        Assert.Null(next);
    }

    [Fact]
    public async Task WhenAMissingClientTopicBecomesReachable_ThenTheNextLookupResolvesIt()
    {
        // Arrange
        var mapper = new CountingMapper(CreateMapper());
        var subject = CreateRootSubject();
        await using var client = CreateClient(subject, mapper);
        Assert.Null(await client.TryGetPropertyForTopicAsync("child/value"));

        // Act
        subject.Child = new MqttCacheTestChild();
        var resolved = await client.TryGetPropertyForTopicAsync("child/value");
        var cached = await client.TryGetPropertyForTopicAsync("child/value");

        // Assert
        Assert.Same(subject.Child, resolved?.Subject);
        Assert.Equal(nameof(MqttCacheTestChild.Value), resolved?.Name);
        Assert.Equal(resolved, cached);
        Assert.Equal(2, mapper.PropertyLookupCount);
    }

    [Fact]
    public async Task WhenAMissingServerPathBecomesReachable_ThenTheNextLookupResolvesIt()
    {
        // Arrange
        var mapper = new CountingMapper(CreateMapper());
        var subject = CreateRootSubject();
        await using var server = CreateServer(subject, mapper);
        Assert.Null(await server.TryGetPropertyForTopicAsync("child/value", CancellationToken.None));

        // Act
        subject.Child = new MqttCacheTestChild();
        var resolved = await server.TryGetPropertyForTopicAsync("child/value", CancellationToken.None);
        var cached = await server.TryGetPropertyForTopicAsync("child/value", CancellationToken.None);

        // Assert
        Assert.Same(subject.Child, resolved?.Subject);
        Assert.Equal(nameof(MqttCacheTestChild.Value), resolved?.Name);
        Assert.Equal(resolved, cached);
        Assert.Equal(2, mapper.PropertyLookupCount);
    }

    [Fact]
    public async Task WhenTheServerFiltersChanges_ThenItAdmitsOnlyScalarPropertiesWithATopic()
    {
        // Arrange
        var subject = CreateRootSubject();
        subject.Child = new MqttCacheTestChild();
        await using var server = CreateServer(subject, CreateMapper());
        var registered = subject.TryGetRegisteredSubject()!;
        var name = registered.TryGetProperty(nameof(MqttCacheTestRoot.Name))!;
        var comment = registered.TryGetProperty(nameof(MqttCacheTestRoot.Comment))!;
        var child = registered.TryGetProperty(nameof(MqttCacheTestRoot.Child))!;

        // Act
        var nameIncluded = server.IsPropertyIncluded(name.Reference);
        var commentIncluded = server.IsPropertyIncluded(comment.Reference);
        var childIncluded = server.IsPropertyIncluded(child.Reference);

        // Assert
        Assert.True(nameIncluded);
        Assert.False(commentIncluded);
        Assert.False(childIncluded);
    }

    [Fact]
    public async Task WhenProviderExcludesAPublishedProperty_ThenTheServerFilterAndPublishDropItsChangesUntilIncludedAgain()
    {
        // Arrange
        var subject = CreateRootSubject();
        var pathProvider = new MutablePathProvider();
        await using var server = CreateServer(subject, new MqttPathProviderMapper(pathProvider));
        var property = subject.TryGetRegisteredSubject()!.TryGetProperty(nameof(MqttCacheTestRoot.Name))!;
        Assert.Equal("name", server.TryGetTopicForProperty(property).Topic);
        Assert.True(server.IsPropertyIncluded(property.Reference));

        // Act
        pathProvider.Included = false;
        var includedWhileExcluded = server.IsPropertyIncluded(property.Reference);
        var topicWhileExcluded = server.TryGetTopicForProperty(property).Topic;
        pathProvider.Included = true;
        var includedAgain = server.IsPropertyIncluded(property.Reference);
        var topicAgain = server.TryGetTopicForProperty(property).Topic;

        // Assert
        Assert.False(includedWhileExcluded);
        Assert.Null(topicWhileExcluded);
        Assert.True(includedAgain);
        Assert.Equal("name", topicAgain);
    }

    [Fact]
    public async Task WhenAPublishedSubjectMovesUnderAnUnmappedPropertyWithoutADetach_ThenTheServerFilterAndPublishDropItsChanges()
    {
        // Arrange
        var subject = CreateRootSubject();
        var child = new MqttCacheTestChild();
        subject.Child = child;
        await using var server = CreateServer(subject, CreateMapper());
        var property = child.TryGetRegisteredSubject()!.TryGetProperty(nameof(MqttCacheTestChild.Value))!;
        Assert.Equal("child/value", server.TryGetTopicForProperty(property).Topic);
        Assert.True(server.IsPropertyIncluded(property.Reference));

        // Act
        subject.Hidden = child;
        subject.Child = null;
        var included = server.IsPropertyIncluded(property.Reference);
        var topic = server.TryGetTopicForProperty(property).Topic;

        // Assert
        Assert.NotNull(child.TryGetRegisteredSubject());
        Assert.False(included);
        Assert.Null(topic);
    }

    [Fact]
    public async Task WhenAPublishedSubjectMovesToAnotherMappedPropertyWithoutADetach_ThenTheServerPublishesItUnderItsNewTopic()
    {
        // Arrange
        var subject = CreateRootSubject();
        var child = new MqttCacheTestChild();
        subject.Child = child;
        subject.Other = child;
        await using var server = CreateServer(subject, CreateMapper());
        var property = child.TryGetRegisteredSubject()!.TryGetProperty(nameof(MqttCacheTestChild.Value))!;
        Assert.Equal("child/value", server.TryGetTopicForProperty(property).Topic);

        // Act
        subject.Child = null;
        var included = server.IsPropertyIncluded(property.Reference);
        var topic = server.TryGetTopicForProperty(property).Topic;

        // Assert
        Assert.NotNull(child.TryGetRegisteredSubject());
        Assert.True(included);
        Assert.Equal("other/value", topic);
    }

    [Fact]
    public async Task WhenASubjectIsDetachedAndAttachedAgain_ThenTheServerFilterAndPublishFollowIt()
    {
        // Arrange
        var mapper = new CountingMapper(CreateMapper());
        var subject = CreateRootSubject();
        var child = new MqttCacheTestChild();
        subject.Child = child;
        await using var server = CreateServer(subject, mapper);
        var value = child.TryGetRegisteredSubject()!.TryGetProperty(nameof(MqttCacheTestChild.Value))!;
        var includedBeforeDetach = server.IsPropertyIncluded(value.Reference);

        // Act
        subject.Child = null;
        var includedAfterDetach = server.IsPropertyIncluded(value.Reference);
        subject.Child = child;
        var includedAfterAttach = server.IsPropertyIncluded(value.Reference);
        var attachedValue = child.TryGetRegisteredSubject()!.TryGetProperty(nameof(MqttCacheTestChild.Value))!;
        var topicAfterAttach = server.TryGetTopicForProperty(attachedValue).Topic;

        // Assert
        Assert.True(includedBeforeDetach);
        Assert.False(includedAfterDetach);
        Assert.True(includedAfterAttach);
        Assert.Equal("child/value", topicAfterAttach);
    }

    [Fact]
    public async Task WhenTheServerLooksUpATopicWithARegistrationDiscardedByAReattach_ThenItComputesTheTopicFromTheCurrentRegistration()
    {
        // Arrange: a publish or filter captured the registration, then another thread detached and attached the subject again.
        var subject = CreateRootSubject();
        var child = new MqttCacheTestChild();
        subject.Child = child;
        await using var server = CreateServer(subject, CreateMapper());
        var reference = new PropertyReference(child, nameof(MqttCacheTestChild.Value));
        var captured = reference.TryGetRegisteredProperty()!;
        subject.Child = null;
        subject.Child = child;
        var current = reference.TryGetRegisteredProperty()!;

        // Act
        var racing = server.TryGetTopicForProperty(captured).Topic;
        var next = server.TryGetTopicForProperty(current).Topic;
        var included = server.IsPropertyIncluded(reference);

        // Assert
        Assert.NotSame(captured, current);
        Assert.Equal("child/value", next);
        Assert.True(included);
        Assert.Equal("child/value", racing);
    }

    [Fact]
    public async Task WhenTheClientLooksUpATopicWithARegistrationDiscardedByAReattach_ThenItComputesTheTopicFromTheCurrentRegistration()
    {
        // Arrange: an outbound write captured the registration, then another thread detached and attached the subject again.
        var subject = CreateRootSubject();
        var child = new MqttCacheTestChild();
        subject.Child = child;
        await using var client = CreateClient(subject, CreateMapper());
        var reference = new PropertyReference(child, nameof(MqttCacheTestChild.Value));
        var captured = reference.TryGetRegisteredProperty()!;
        subject.Child = null;
        subject.Child = child;
        var current = reference.TryGetRegisteredProperty()!;

        // Act
        var racing = client.TryGetTopicForProperty(reference, captured).Topic;
        var next = client.TryGetTopicForProperty(reference, current).Topic;

        // Assert
        Assert.NotSame(captured, current);
        Assert.Equal("child/value", next);
        Assert.Equal("child/value", racing);
    }

    [Fact]
    public async Task WhenASubjectIsDetachedAndAttachedAgainWhileTheClientSubscribes_ThenItSubscribesAndCachesTheTopicOfTheCurrentRegistration()
    {
        // Arrange: the subscribe loop captures every registration up front, and the reattach lands after the
        // loop mapped the root's Name, standing in for one that lands while an earlier batch awaits its SUBACK.
        var subject = CreateRootSubject();
        var child = new MqttCacheTestChild();
        subject.Child = child;
        var mapper = new HookedMapper(
            CreateMapper(),
            new PropertyReference(subject, nameof(MqttCacheTestRoot.Name)),
            lookupNumber: 2,
            () =>
            {
                subject.Child = null;
                subject.Child = child;
            });
        await using var client = CreateClient(subject, mapper);
        var subscribedTopics = new List<string>();
        var mqttClient = new Mock<IMqttClient>();
        mqttClient
            .Setup(c => c.SubscribeAsync(It.IsAny<MqttClientSubscribeOptions>(), It.IsAny<CancellationToken>()))
            .Callback<MqttClientSubscribeOptions, CancellationToken>((options, _) =>
                subscribedTopics.AddRange(options.TopicFilters.Select(filter => filter.Topic)));
        typeof(MqttSubjectClientSource)
            .GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(client, mqttClient.Object);
        var subscribe = typeof(MqttSubjectClientSource)
            .GetMethod("SubscribeToPropertiesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<CancellationToken, Task>>(client);
        var reference = new PropertyReference(child, nameof(MqttCacheTestChild.Value));

        // Act
        await subscribe(CancellationToken.None);
        var current = reference.TryGetRegisteredProperty()!;
        var topic = client.TryGetTopicForProperty(reference, current).Topic;

        // Assert
        Assert.True(mapper.HasRun);
        Assert.Equal("child/value", topic);
        Assert.Contains("child/value", subscribedTopics);
    }

    [Fact]
    public async Task WhenASubjectIsDetachedWhileTheClientSubscribes_ThenItKeepsNoTopicEntryClaimOrSubscriptionForIt()
    {
        // Arrange: the detach lands while the subscribe loop computes the topic of the child's property, after
        // the loop's filter mapped it, so the detach clears the caches before the loop inserts its entry.
        var subject = CreateRootSubject();
        var child = new MqttCacheTestChild();
        subject.Child = child;
        var reference = new PropertyReference(child, nameof(MqttCacheTestChild.Value));
        var mapper = new HookedMapper(CreateMapper(), reference, lookupNumber: 2, () => subject.Child = null);
        await using var client = CreateClient(subject, mapper);
        var subscribedTopics = new List<string>();
        var mqttClient = new Mock<IMqttClient>();
        mqttClient
            .Setup(c => c.SubscribeAsync(It.IsAny<MqttClientSubscribeOptions>(), It.IsAny<CancellationToken>()))
            .Callback<MqttClientSubscribeOptions, CancellationToken>((options, _) =>
                subscribedTopics.AddRange(options.TopicFilters.Select(filter => filter.Topic)));
        typeof(MqttSubjectClientSource)
            .GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(client, mqttClient.Object);
        var subscribe = typeof(MqttSubjectClientSource)
            .GetMethod("SubscribeToPropertiesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<CancellationToken, Task>>(client);

        // Act
        await subscribe(CancellationToken.None);

        // Assert
        Assert.True(mapper.HasRun);
        Assert.False(client.HasCachedProperty("child/value"));
        Assert.False(reference.TryGetSource(out _));
        Assert.DoesNotContain("child/value", subscribedTopics);
    }

    [Fact]
    public async Task WhenASubjectMovesFromAnUnmappedToAMappedPropertyWithoutADetach_ThenTheServerFilterAdmitsItsProperty()
    {
        // Arrange
        var mapper = new CountingMapper(CreateMapper());
        var subject = CreateRootSubject();
        var child = new MqttCacheTestChild();
        subject.Hidden = child;
        await using var server = CreateServer(subject, mapper);
        var value = child.TryGetRegisteredSubject()!.TryGetProperty(nameof(MqttCacheTestChild.Value))!;
        var includedWhileUnmapped = server.IsPropertyIncluded(value.Reference);

        // Act
        subject.Child = child;
        subject.Hidden = null;
        var includedWhileMapped = server.IsPropertyIncluded(value.Reference);

        // Assert
        Assert.False(includedWhileUnmapped);
        Assert.True(includedWhileMapped);
        Assert.Equal("child/value", server.TryGetTopicForProperty(value).Topic);
    }

    [Fact]
    public async Task WhenAHolderMovesFromAnUnmappedToAMappedPropertyWithoutADetach_ThenTheServerFilterAdmitsThePropertiesBelowIt()
    {
        // Arrange
        var mapper = new CountingMapper(CreateMapper());
        var subject = CreateRootSubject();
        var leaf = new MqttCacheTestChild();
        var holder = new MqttCacheTestChild { Inner = leaf };
        subject.Hidden = holder;
        await using var server = CreateServer(subject, mapper);
        var value = leaf.TryGetRegisteredSubject()!.TryGetProperty(nameof(MqttCacheTestChild.Value))!;
        var includedWhileUnmapped = server.IsPropertyIncluded(value.Reference);

        // Act
        subject.Child = holder;
        subject.Hidden = null;
        var includedWhileMapped = server.IsPropertyIncluded(value.Reference);

        // Assert
        Assert.False(includedWhileUnmapped);
        Assert.True(includedWhileMapped);
        Assert.Equal("child/inner/value", server.TryGetTopicForProperty(value).Topic);
    }

    [Fact]
    public async Task WhenTheServerFiltersAChangeOfAPropertyWithoutATopic_ThenNothingIsAllocated()
    {
        // Arrange: a property without a topic is looked up again on every change, so the lookup must not allocate.
        var subject = CreateRootSubject();
        await using var server = CreateServer(subject, CreateMapper());
        var comment = subject.TryGetRegisteredSubject()!.TryGetProperty(nameof(MqttCacheTestRoot.Comment))!.Reference;

        // Warm up: the JIT and the provider's per-type caches are one-time costs.
        for (var warmup = 0; warmup < 100; warmup++)
        {
            server.IsPropertyIncluded(comment);
        }

        // Act
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var included = server.IsPropertyIncluded(comment);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        // Assert
        Assert.False(included);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public async Task WhenTheStructureChangesAnywhere_ThenTheServerComputesTheTopicOnceMore()
    {
        // Arrange
        var mapper = new CountingMapper(CreateMapper());
        var subject = CreateRootSubject();
        await using var server = CreateServer(subject, mapper);
        var property = subject.TryGetRegisteredSubject()!.TryGetProperty(nameof(MqttCacheTestRoot.Name))!;
        server.TryGetTopicForProperty(property);

        // Act
        subject.Hidden = new MqttCacheTestChild();
        var afterChange = server.TryGetTopicForProperty(property).Topic;
        var cached = server.TryGetTopicForProperty(property).Topic;

        // Assert
        Assert.Equal("name", afterChange);
        Assert.Equal("name", cached);
        Assert.Equal(2, mapper.MappingLookupCount);
    }

    [Fact]
    public async Task WhenACollectionIsReordered_ThenTheServerPublishesUnderTheNewPosition()
    {
        // Arrange
        var subject = CreateRootSubject();
        var first = new MqttCacheTestChild();
        var second = new MqttCacheTestChild();
        subject.Items = [first, second];
        await using var server = CreateServer(subject, CreateMapper());
        var property = first.TryGetRegisteredSubject()!.TryGetProperty(nameof(MqttCacheTestChild.Value))!;
        Assert.Equal("items[0]/value", server.TryGetTopicForProperty(property).Topic);

        // Act
        subject.Items = [second, first];
        var topic = server.TryGetTopicForProperty(property).Topic;

        // Assert
        Assert.Equal("items[1]/value", topic);
    }

    [Fact]
    public async Task WhenACollectionIsReordered_ThenTheClientPublishesUnderTheNewPosition()
    {
        // Arrange
        var subject = CreateRootSubject();
        var first = new MqttCacheTestChild();
        var second = new MqttCacheTestChild();
        subject.Items = [first, second];
        await using var client = CreateClient(subject, CreateMapper());
        var property = first.TryGetRegisteredSubject()!.TryGetProperty(nameof(MqttCacheTestChild.Value))!;
        Assert.Equal("items[0]/value", client.TryGetTopicForProperty(property.Reference, property).Topic);

        // Act
        subject.Items = [second, first];
        var topic = client.TryGetTopicForProperty(property.Reference, property).Topic;

        // Assert
        Assert.Equal("items[1]/value", topic);
    }

    [Fact]
    public async Task WhenADictionaryEntryIsReKeyed_ThenTheServerPublishesUnderTheNewKey()
    {
        // Arrange
        var subject = CreateRootSubject();
        var child = new MqttCacheTestChild();
        subject.Map = new Dictionary<string, MqttCacheTestChild> { ["a"] = child };
        await using var server = CreateServer(subject, CreateMapper());
        var property = child.TryGetRegisteredSubject()!.TryGetProperty(nameof(MqttCacheTestChild.Value))!;
        Assert.Equal("map[a]/value", server.TryGetTopicForProperty(property).Topic);

        // Act
        subject.Map = new Dictionary<string, MqttCacheTestChild> { ["b"] = child };
        var topic = server.TryGetTopicForProperty(property).Topic;

        // Assert
        Assert.Equal("map[b]/value", topic);
    }

    [Fact]
    public async Task WhenADictionaryEntryIsReKeyed_ThenTheClientPublishesUnderTheNewKey()
    {
        // Arrange
        var subject = CreateRootSubject();
        var child = new MqttCacheTestChild();
        subject.Map = new Dictionary<string, MqttCacheTestChild> { ["a"] = child };
        await using var client = CreateClient(subject, CreateMapper());
        var property = child.TryGetRegisteredSubject()!.TryGetProperty(nameof(MqttCacheTestChild.Value))!;
        Assert.Equal("map[a]/value", client.TryGetTopicForProperty(property.Reference, property).Topic);

        // Act
        subject.Map = new Dictionary<string, MqttCacheTestChild> { ["b"] = child };
        var topic = client.TryGetTopicForProperty(property.Reference, property).Topic;

        // Assert
        Assert.Equal("map[b]/value", topic);
    }

    [Fact]
    public async Task WhenTheStructureChangesWhileTheServerComputesATopic_ThenTheNextLookupComputesItAgain()
    {
        // Arrange
        var mapper = new CountingMapper(CreateMapper());
        var subject = CreateRootSubject();
        var child = new MqttCacheTestChild();
        subject.Child = child;
        await using var server = CreateServer(subject, mapper);
        var property = child.TryGetRegisteredSubject()!.TryGetProperty(nameof(MqttCacheTestChild.Value))!;
        mapper.DuringNextMapping = () =>
        {
            subject.Other = child;
            subject.Child = null;
        };

        // Act
        var racing = server.TryGetTopicForProperty(property).Topic;
        var next = server.TryGetTopicForProperty(property).Topic;

        // Assert
        Assert.Equal("child/value", racing);
        Assert.Equal("other/value", next);
    }

    [Fact]
    public async Task WhenASubjectMovesWithoutADetach_ThenTheServerNoLongerResolvesItsOldTopic()
    {
        // Arrange
        var subject = CreateRootSubject();
        var child = new MqttCacheTestChild();
        subject.Child = child;
        await using var server = CreateServer(subject, CreateMapper());
        Assert.Same(child, (await server.TryGetPropertyForTopicAsync("child/value", CancellationToken.None))?.Subject);

        // Act
        subject.Other = child;
        subject.Child = null;
        var oldTopic = await server.TryGetPropertyForTopicAsync("child/value", CancellationToken.None);
        var newTopic = await server.TryGetPropertyForTopicAsync("other/value", CancellationToken.None);

        // Assert
        Assert.Null(oldTopic);
        Assert.Same(child, newTopic?.Subject);
    }

    [Fact]
    public async Task WhenACollectionIsReordered_ThenTheServerResolvesThePositionToTheSubjectNowThere()
    {
        // Arrange
        var subject = CreateRootSubject();
        var first = new MqttCacheTestChild();
        var second = new MqttCacheTestChild();
        subject.Items = [first, second];
        await using var server = CreateServer(subject, CreateMapper());
        Assert.Same(first, (await server.TryGetPropertyForTopicAsync("items[0]/value", CancellationToken.None))?.Subject);

        // Act
        subject.Items = [second, first];
        var resolved = await server.TryGetPropertyForTopicAsync("items[0]/value", CancellationToken.None);

        // Assert
        Assert.Same(second, resolved?.Subject);
    }

    [Fact]
    public async Task WhenACollectionIsReordered_ThenTheClientResolvesThePositionToTheSubjectNowThere()
    {
        // Arrange
        var subject = CreateRootSubject();
        var first = new MqttCacheTestChild();
        var second = new MqttCacheTestChild();
        subject.Items = [first, second];
        await using var client = CreateClient(subject, CreateMapper());
        Assert.Same(first, (await client.TryGetPropertyForTopicAsync("items[0]/value"))?.Subject);

        // Act
        subject.Items = [second, first];
        var resolved = await client.TryGetPropertyForTopicAsync("items[0]/value");

        // Assert
        Assert.Same(second, resolved?.Subject);
    }

    [Fact]
    public async Task WhenProviderExcludesAResolvedProperty_ThenTheServerNoLongerResolvesItsTopic()
    {
        // Arrange
        var subject = CreateRootSubject();
        var pathProvider = new MutablePathProvider();
        await using var server = CreateServer(subject, new MqttPathProviderMapper(pathProvider));
        Assert.NotNull(await server.TryGetPropertyForTopicAsync("name", CancellationToken.None));

        // Act
        pathProvider.Included = false;
        var resolved = await server.TryGetPropertyForTopicAsync("name", CancellationToken.None);

        // Assert
        Assert.Null(resolved);
    }

    [Fact]
    public async Task WhenAPublishedSubjectMovesToAnotherMappedPropertyWithoutADetach_ThenTheClientPublishesItUnderItsNewTopic()
    {
        // Arrange
        var subject = CreateRootSubject();
        var child = new MqttCacheTestChild();
        subject.Child = child;
        subject.Other = child;
        await using var client = CreateClient(subject, CreateMapper());
        var property = child.TryGetRegisteredSubject()!.TryGetProperty(nameof(MqttCacheTestChild.Value))!;
        Assert.Equal("child/value", client.TryGetTopicForProperty(property.Reference, property).Topic);

        // Act
        subject.Child = null;
        var topic = client.TryGetTopicForProperty(property.Reference, property).Topic;

        // Assert
        Assert.Equal("other/value", topic);
    }

    [Fact]
    public async Task WhenASubjectMovesFromAnUnmappedToAMappedPropertyWithoutADetach_ThenTheClientPublishesItsProperty()
    {
        // Arrange
        var subject = CreateRootSubject();
        var child = new MqttCacheTestChild();
        subject.Hidden = child;
        await using var client = CreateClient(subject, CreateMapper());
        var property = child.TryGetRegisteredSubject()!.TryGetProperty(nameof(MqttCacheTestChild.Value))!;
        Assert.Null(client.TryGetTopicForProperty(property.Reference, property).Topic);

        // Act
        subject.Child = child;
        subject.Hidden = null;
        var topic = client.TryGetTopicForProperty(property.Reference, property).Topic;

        // Assert
        Assert.Equal("child/value", topic);
    }

    [Fact]
    public async Task WhenProviderExcludesAPublishedProperty_ThenTheClientDropsItsTopicUntilIncludedAgain()
    {
        // Arrange
        var subject = CreateRootSubject();
        var pathProvider = new MutablePathProvider();
        await using var client = CreateClient(subject, new MqttPathProviderMapper(pathProvider));
        var property = subject.TryGetRegisteredSubject()!.TryGetProperty(nameof(MqttCacheTestRoot.Name))!;
        Assert.Equal("name", client.TryGetTopicForProperty(property.Reference, property).Topic);

        // Act
        pathProvider.Included = false;
        var topicWhileExcluded = client.TryGetTopicForProperty(property.Reference, property).Topic;
        pathProvider.Included = true;
        var topicAgain = client.TryGetTopicForProperty(property.Reference, property).Topic;

        // Assert
        Assert.Null(topicWhileExcluded);
        Assert.Equal("name", topicAgain);
    }

    [Fact]
    public async Task WhenASubjectMovesWithoutADetach_ThenTheClientNoLongerResolvesItsOldTopic()
    {
        // Arrange
        var subject = CreateRootSubject();
        var child = new MqttCacheTestChild();
        subject.Child = child;
        await using var client = CreateClient(subject, CreateMapper());
        Assert.Same(child, (await client.TryGetPropertyForTopicAsync("child/value"))?.Subject);

        // Act
        subject.Other = child;
        subject.Child = null;
        var oldTopic = await client.TryGetPropertyForTopicAsync("child/value");
        var oldTopicRetained = client.HasCachedProperty("child/value");
        var newTopic = await client.TryGetPropertyForTopicAsync("other/value");

        // Assert
        Assert.Null(oldTopic);
        Assert.False(oldTopicRetained);
        Assert.Same(child, newTopic?.Subject);
    }

    [Fact]
    public async Task WhenADictionaryEntryIsReKeyed_ThenTheClientResolvesTheNewKey()
    {
        // Arrange
        var subject = CreateRootSubject();
        var child = new MqttCacheTestChild();
        subject.Map = new Dictionary<string, MqttCacheTestChild> { ["a"] = child };
        await using var client = CreateClient(subject, CreateMapper());
        Assert.Same(child, (await client.TryGetPropertyForTopicAsync("map[a]/value"))?.Subject);

        // Act
        subject.Map = new Dictionary<string, MqttCacheTestChild> { ["b"] = child };
        var oldKey = await client.TryGetPropertyForTopicAsync("map[a]/value");
        var oldKeyRetained = client.HasCachedProperty("map[a]/value");
        var newKey = await client.TryGetPropertyForTopicAsync("map[b]/value");

        // Assert
        Assert.Null(oldKey);
        Assert.False(oldKeyRetained);
        Assert.Same(child, newKey?.Subject);
    }

    [Fact]
    public async Task WhenADictionaryEntryIsReKeyed_ThenTheServerResolvesTheNewKey()
    {
        // Arrange
        var subject = CreateRootSubject();
        var child = new MqttCacheTestChild();
        subject.Map = new Dictionary<string, MqttCacheTestChild> { ["a"] = child };
        await using var server = CreateServer(subject, CreateMapper());
        Assert.Same(child, (await server.TryGetPropertyForTopicAsync("map[a]/value", CancellationToken.None))?.Subject);

        // Act
        subject.Map = new Dictionary<string, MqttCacheTestChild> { ["b"] = child };
        var oldKey = await server.TryGetPropertyForTopicAsync("map[a]/value", CancellationToken.None);
        var oldKeyRetained = server.HasCachedPath("map[a]/value");
        var newKey = await server.TryGetPropertyForTopicAsync("map[b]/value", CancellationToken.None);

        // Assert
        Assert.Null(oldKey);
        Assert.False(oldKeyRetained);
        Assert.Same(child, newKey?.Subject);
    }

    [Fact]
    public async Task WhenTheStructureChangesWhileTheClientComputesATopic_ThenTheNextLookupComputesItAgain()
    {
        // Arrange
        var mapper = new CountingMapper(CreateMapper());
        var subject = CreateRootSubject();
        var child = new MqttCacheTestChild();
        subject.Child = child;
        await using var client = CreateClient(subject, mapper);
        var property = child.TryGetRegisteredSubject()!.TryGetProperty(nameof(MqttCacheTestChild.Value))!;
        mapper.DuringNextMapping = () =>
        {
            subject.Other = child;
            subject.Child = null;
        };

        // Act
        var racing = client.TryGetTopicForProperty(property.Reference, property).Topic;
        var next = client.TryGetTopicForProperty(property.Reference, property).Topic;

        // Assert
        Assert.Equal("child/value", racing);
        Assert.Equal("other/value", next);
    }

    [Fact]
    public async Task WhenTheStructureChangesWhileTheClientResolvesATopic_ThenTheNextLookupResolvesItAgain()
    {
        // Arrange
        var mapper = new CountingMapper(CreateMapper());
        var subject = CreateRootSubject();
        var child = new MqttCacheTestChild();
        subject.Child = child;
        await using var client = CreateClient(subject, mapper);
        mapper.DuringNextPropertyLookup = () =>
        {
            subject.Other = child;
            subject.Child = null;
        };

        // Act
        var racing = await client.TryGetPropertyForTopicAsync("child/value");
        var next = await client.TryGetPropertyForTopicAsync("child/value");

        // Assert
        Assert.Equal(nameof(MqttCacheTestChild.Value), racing?.Name);
        Assert.Null(next);
    }

    [Fact]
    public async Task WhenTheClientLooksUpACachedTopic_ThenNothingIsAllocated()
    {
        // Arrange: every published change looks the topic up again, so a cache hit must not allocate.
        var subject = CreateRootSubject();
        await using var client = CreateClient(subject, CreateMapper());
        var property = subject.TryGetRegisteredSubject()!.TryGetProperty(nameof(MqttCacheTestRoot.Name))!;

        // Warm up: the JIT and the provider's per-type caches are one-time costs.
        for (var warmup = 0; warmup < 100; warmup++)
        {
            client.TryGetTopicForProperty(property.Reference, property);
        }

        // Act
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var topic = client.TryGetTopicForProperty(property.Reference, property).Topic;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        // Assert
        Assert.Equal("name", topic);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public async Task WhenProviderExcludesAResolvedProperty_ThenTheClientNoLongerResolvesItsTopic()
    {
        // Arrange
        var subject = CreateRootSubject();
        var pathProvider = new MutablePathProvider();
        await using var client = CreateClient(subject, new MqttPathProviderMapper(pathProvider));
        Assert.NotNull(await client.TryGetPropertyForTopicAsync("name"));

        // Act
        pathProvider.Included = false;
        var resolved = await client.TryGetPropertyForTopicAsync("name");

        // Assert
        Assert.Null(resolved);
    }

    [Fact]
    public async Task WhenATopicResolvesToAPropertyTheClientOwns_ThenItIsApplied()
    {
        // Arrange
        var subject = CreateRootSubject();
        var child = new MqttCacheTestChild();
        subject.Child = child;
        await using var client = CreateClient(subject, CreateMapper());
        var property = new PropertyReference(child, nameof(MqttCacheTestChild.Value));
        client.Ownership.ClaimSource(property);

        // Act
        var resolved = await client.TryGetOwnedPropertyForTopicAsync("child/value");

        // Assert
        Assert.Equal(property, resolved);
    }

    [Fact]
    public async Task WhenATopicResolvesToAPropertyTheClientDoesNotOwn_ThenItIsNotApplied()
    {
        // Arrange
        var subject = CreateRootSubject();
        var child = new MqttCacheTestChild();
        subject.Child = child;
        await using var client = CreateClient(subject, CreateMapper());
        client.Ownership.ClaimSource(new PropertyReference(child, nameof(MqttCacheTestChild.Value)));
        subject.Other = child;
        subject.Child = null;
        var newChild = new MqttCacheTestChild();
        subject.Child = newChild;

        // Act
        var resolved = await client.TryGetPropertyForTopicAsync("child/value");
        var owned = await client.TryGetOwnedPropertyForTopicAsync("child/value");

        // Assert
        Assert.Same(newChild, resolved?.Subject);
        Assert.Null(owned);
    }

    private sealed class MutablePathProvider() : AttributeBasedPathProvider("mqtt", '/')
    {
        private long _version;
        private volatile bool _included = true;

        public bool Included
        {
            get => _included;
            set
            {
                _included = value;
                Interlocked.Increment(ref _version);
            }
        }

        public override long Version => Volatile.Read(ref _version);

        public override bool TryGetPropertySegment(RegisteredSubjectProperty property, [NotNullWhen(true)] out string? segment)
        {
            if (!Included)
            {
                segment = null;
                return false;
            }

            return base.TryGetPropertySegment(property, out segment);
        }
    }

    private static MqttCacheTestRoot CreateRootSubject()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        return new MqttCacheTestRoot(context);
    }

    /// <summary>
    /// Attaches a child, captures the registration of one of its properties, then removes the child
    /// again. The returned registration is deliberately stale: the caches must refuse it.
    /// </summary>
    private static RegisteredSubjectProperty DetachChildAndReturnStaleProperty(MqttCacheTestRoot root)
    {
        root.Child = new MqttCacheTestChild();
        var property = root.Child.TryGetRegisteredSubject()!.TryGetProperty(nameof(MqttCacheTestChild.Value))!;
        root.Child = null;

        Assert.Null(property.Subject.TryGetRegisteredSubject());
        return property;
    }

    /// <summary>
    /// Attaches a child, then detaches it while <paramref name="insert"/> runs from a SubjectDetaching
    /// subscriber added after the connector's own. That is the deterministic stand-in for a publish or
    /// an inbound message whose lookup lands between the connector's detach cleanup and the registry
    /// deregistration that follows it. Returns the child's now stale registration together with what
    /// the insert observed, which proves the registry was still intact at that point.
    /// </summary>
    private static (RegisteredSubjectProperty Property, string? Observed) DetachChildWhileInserting(
        MqttCacheTestRoot root,
        Func<RegisteredSubjectProperty, string?> insert)
    {
        var child = new MqttCacheTestChild();
        root.Child = child;

        var property = child.TryGetRegisteredSubject()!.TryGetProperty(nameof(MqttCacheTestChild.Value))!;
        var lifecycle = ((IInterceptorSubject)root).Context.TryGetLifecycleInterceptor()!;

        string? observed = null;

        void Handler(SubjectLifecycleChange change)
        {
            if (ReferenceEquals(change.Subject, child))
            {
                observed = insert(property);
            }
        }

        lifecycle.SubjectDetaching += Handler;
        try
        {
            root.Child = null;
        }
        finally
        {
            lifecycle.SubjectDetaching -= Handler;
        }

        Assert.Null(child.TryGetRegisteredSubject());
        return (property, observed);
    }

    private static T SyncResult<T>(ValueTask<T> task)
    {
        Assert.True(task.IsCompleted, "The mapper is expected to resolve synchronously.");
        return task.Result;
    }

    private static MqttSubjectServer CreateServer(
        IInterceptorSubject subject,
        IReversePropertyMapper<MqttPropertyMapping, MqttLookupKey> mapper)
    {
        return new MqttSubjectServer(
            subject,
            new MqttServerConfiguration { Mapper = mapper },
            NullLogger<MqttSubjectServer>.Instance);
    }

    private static MqttSubjectClientSource CreateClient(
        IInterceptorSubject subject,
        IReversePropertyMapper<MqttPropertyMapping, MqttLookupKey> mapper)
    {
        return new MqttSubjectClientSource(
            subject,
            new MqttClientConfiguration { BrokerHost = "127.0.0.1", Mapper = mapper },
            NullLogger<MqttSubjectClientSource>.Instance);
    }

    private static MqttCompositeMapper CreateMapper() => new(
        new MqttPathProviderMapper(new AttributeBasedPathProvider("mqtt", '/')),
        new MqttAttributeMapper("mqtt"));

    /// <summary>
    /// Counts how often the caches fall through to the mapper, which is the only externally visible
    /// signal that a lookup was not served from cache.
    /// </summary>
    private sealed class CountingMapper : IReversePropertyMapper<MqttPropertyMapping, MqttLookupKey>
    {
        private readonly IReversePropertyMapper<MqttPropertyMapping, MqttLookupKey> _inner;

        private int _mappingLookupCount;
        private int _propertyLookupCount;

        public CountingMapper(IReversePropertyMapper<MqttPropertyMapping, MqttLookupKey> inner)
        {
            _inner = inner;
        }

        public int MappingLookupCount => Volatile.Read(ref _mappingLookupCount);

        public int PropertyLookupCount => Volatile.Read(ref _propertyLookupCount);

        /// <summary>
        /// When set, property lookups return this property instead of resolving the key. Stands in for a lookup
        /// that resolved before a detach began, since a path through a detaching subject no longer resolves.
        /// </summary>
        public RegisteredSubjectProperty? ResolvedPropertyOverride { get; set; }

        /// <summary>
        /// When set, runs once inside the next forward lookup, after the inner mapper answered: a structural change
        /// that lands while a cache computes its entry.
        /// </summary>
        public Action? DuringNextMapping { get; set; }

        /// <summary>
        /// When set, runs once inside the next reverse lookup, after the inner mapper answered: a structural change
        /// that lands while a cache resolves its entry.
        /// </summary>
        public Action? DuringNextPropertyLookup { get; set; }

        public long Version => _inner.Version;

        public bool TryGetMapping(
            RegisteredSubjectProperty property,
            IInterceptorSubject rootSubject,
            [NotNullWhen(true)] out MqttPropertyMapping? mapping)
        {
            Interlocked.Increment(ref _mappingLookupCount);
            var found = _inner.TryGetMapping(property, rootSubject, out mapping);
            var duringMapping = DuringNextMapping;
            DuringNextMapping = null;
            duringMapping?.Invoke();
            return found;
        }

        public ValueTask<RegisteredSubjectProperty?> TryGetPropertyAsync(
            MqttLookupKey key,
            RegisteredSubject subject,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _propertyLookupCount);
            var result = ResolvedPropertyOverride is { } property
                ? ValueTask.FromResult<RegisteredSubjectProperty?>(property)
                : _inner.TryGetPropertyAsync(key, subject, cancellationToken);
            var duringLookup = DuringNextPropertyLookup;
            DuringNextPropertyLookup = null;
            duringLookup?.Invoke();
            return result;
        }
    }

    /// <summary>
    /// Runs an action once, inside the given forward lookup of one property, after the inner mapper answered: a
    /// structural change that lands at a chosen point of a loop over several properties.
    /// </summary>
    private sealed class HookedMapper(
        IReversePropertyMapper<MqttPropertyMapping, MqttLookupKey> inner,
        PropertyReference hookedProperty,
        int lookupNumber,
        Action action) : IReversePropertyMapper<MqttPropertyMapping, MqttLookupKey>
    {
        private int _lookupCount;

        public bool HasRun { get; private set; }

        public long Version => inner.Version;

        public bool TryGetMapping(
            RegisteredSubjectProperty property,
            IInterceptorSubject rootSubject,
            [NotNullWhen(true)] out MqttPropertyMapping? mapping)
        {
            var found = inner.TryGetMapping(property, rootSubject, out mapping);
            if (property.Reference == hookedProperty && ++_lookupCount == lookupNumber)
            {
                HasRun = true;
                action();
            }

            return found;
        }

        public ValueTask<RegisteredSubjectProperty?> TryGetPropertyAsync(
            MqttLookupKey key,
            RegisteredSubject subject,
            CancellationToken cancellationToken) =>
            inner.TryGetPropertyAsync(key, subject, cancellationToken);
    }
}

[InterceptorSubject]
public partial class MqttCacheTestRoot
{
    [Path("mqtt", "name")]
    public partial string Name { get; set; }

    [Path("mqtt", "child")]
    public partial MqttCacheTestChild? Child { get; set; }

    [Path("mqtt", "other")]
    public partial MqttCacheTestChild? Other { get; set; }

    public partial string? Comment { get; set; }

    public partial MqttCacheTestChild? Hidden { get; set; }

    [Path("mqtt", "items")]
    public partial MqttCacheTestChild[] Items { get; set; }

    [Path("mqtt", "map")]
    public partial Dictionary<string, MqttCacheTestChild> Map { get; set; }

    public MqttCacheTestRoot()
    {
        Name = string.Empty;
        Items = [];
        Map = new Dictionary<string, MqttCacheTestChild>();
    }
}

[InterceptorSubject]
public partial class MqttCacheTestChild
{
    [Path("mqtt", "value")]
    public partial string Value { get; set; }

    [Path("mqtt", "inner")]
    public partial MqttCacheTestChild? Inner { get; set; }

    public MqttCacheTestChild()
    {
        Value = string.Empty;
    }
}
