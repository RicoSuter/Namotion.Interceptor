using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Connectors.Mapping;
using Namotion.Interceptor.Mqtt.Client;
using Namotion.Interceptor.Mqtt.Mapping;
using Namotion.Interceptor.Mqtt.Server;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Mqtt.Tests;

/// <summary>
/// Both connectors add a topic-cache entry and then validate it, so an entry added while its subject
/// was being released cannot outlive the detach sweep that already passed. Registry membership is
/// what that validation asks. The reference count cannot answer it: it counts incoming structural
/// edges and is zero for the connector's own root, so asking it there evicts every mapping the root
/// owns and turns the cache into a per-message mapper call.
/// </summary>
public class MqttTopicCacheTests
{
    [Fact]
    public async Task WhenTheServerMapsAPropertyOfItsAnchoredRoot_ThenTheTopicMappingStaysCached()
    {
        // Arrange
        var root = CreateRoot();
        var mapper = new CountingMqttMapper();
        await using var server = new MqttSubjectServer(
            root, new MqttServerConfiguration { Mapper = mapper }, NullLogger<MqttSubjectServer>.Instance);

        var property = root.TryGetRegisteredSubject()!.TryGetProperty(nameof(TopicCacheRoot.Name))!;

        // Act
        server.TryGetTopicForProperty(property.Reference, property);
        server.TryGetTopicForProperty(property.Reference, property);

        // Assert: an anchored root has no incoming edge, which says nothing about whether it is
        // attached, so the second lookup must be served from the cache.
        Assert.Equal(0, root.GetReferenceCount());
        Assert.Equal(1, mapper.MappingLookups);
    }

    [Fact]
    public void WhenTheClientMapsAPropertyOfItsAnchoredRoot_ThenTheTopicMappingStaysCached()
    {
        // Arrange
        var root = CreateRoot();
        var mapper = new CountingMqttMapper();
        using var client = new MqttSubjectClientSource(
            root,
            new MqttClientConfiguration { BrokerHost = "127.0.0.1", Mapper = mapper },
            NullLogger<MqttSubjectClientSource>.Instance);

        var property = root.TryGetRegisteredSubject()!.TryGetProperty(nameof(TopicCacheRoot.Name))!;

        // Act
        client.TryGetTopicForProperty(property.Reference, property);
        client.TryGetTopicForProperty(property.Reference, property);

        // Assert
        Assert.Equal(0, root.GetReferenceCount());
        Assert.Equal(1, mapper.MappingLookups);
    }

    [Fact]
    public async Task WhenTheSubjectLeftTheRegistry_ThenItsTopicMappingIsNotCached()
    {
        // Arrange
        var root = CreateRoot();
        var mapper = new CountingMqttMapper();
        await using var server = new MqttSubjectServer(
            root, new MqttServerConfiguration { Mapper = mapper }, NullLogger<MqttSubjectServer>.Instance);

        root.Child = new TopicCacheChild { Value = "child" };
        var property = root.Child.TryGetRegisteredSubject()!.TryGetProperty(nameof(TopicCacheChild.Value))!;

        // Act: the subject leaves the graph, so its detach sweep has already run and nothing would
        // ever remove an entry cached for it afterwards.
        root.Child = null;
        server.TryGetTopicForProperty(property.Reference, property);
        server.TryGetTopicForProperty(property.Reference, property);

        // Assert
        Assert.Equal(2, mapper.MappingLookups);
    }

    private static TopicCacheRoot CreateRoot()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        return new TopicCacheRoot(context) { Name = "root" };
    }

    private sealed class CountingMqttMapper : IReversePropertyMapper<MqttPropertyMapping, MqttLookupKey>
    {
        public int MappingLookups { get; private set; }

        public bool TryGetMapping(
            RegisteredSubjectProperty property,
            IInterceptorSubject rootSubject,
            [NotNullWhen(true)] out MqttPropertyMapping? mapping)
        {
            MappingLookups++;
            mapping = new MqttPropertyMapping(Topic: property.Name);
            return true;
        }

        public ValueTask<RegisteredSubjectProperty?> TryGetPropertyAsync(
            MqttLookupKey key,
            RegisteredSubject subject,
            CancellationToken cancellationToken)
        {
            return new ValueTask<RegisteredSubjectProperty?>((RegisteredSubjectProperty?)null);
        }
    }
}

[InterceptorSubject]
public partial class TopicCacheRoot
{
    public partial string? Name { get; set; }

    public partial TopicCacheChild? Child { get; set; }
}

[InterceptorSubject]
public partial class TopicCacheChild
{
    public partial string? Value { get; set; }
}
