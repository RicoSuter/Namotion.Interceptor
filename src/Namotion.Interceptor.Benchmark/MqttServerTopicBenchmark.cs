using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Mqtt.Mapping;
using Namotion.Interceptor.Mqtt.Server;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Attributes;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Benchmark;

#pragma warning disable CS8618

/// <summary>
/// The MQTT server's work per change before serialization and the broker: the change filter, and for a published
/// change also the topic lookup of the publish.
/// </summary>
[MemoryDiagnoser]
public class MqttServerTopicBenchmark
{
    private MqttSubjectServer _server;
    private RegisteredSubjectProperty _mapped;
    private PropertyReference _unmapped;

    [Params("Attribute", "Fluent")]
    public string Mapper { get; set; }

    [Params(1, 4)]
    public int Depth { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var root = new MqttBenchmarkNode(context);
        var leaf = root;
        for (var level = 1; level < Depth; level++)
        {
            leaf.Child = new MqttBenchmarkNode();
            leaf = leaf.Child;
        }

        var mapper = Mapper == "Attribute"
            ? new MqttCompositeMapper(
                new MqttPathProviderMapper(new AttributeBasedPathProvider("mqtt", '/')),
                new MqttAttributeMapper("mqtt"))
            : new MqttCompositeMapper(new MqttFluentMapperBuilder<MqttBenchmarkNode>()
                .ForType<MqttBenchmarkNode>()
                .Map(node => node.Name, property => property.WithSegment("name"))
                .Map(node => node.Child, property => property.WithSegment("child"))
                .Build());

        _server = new MqttSubjectServer(
            root,
            new MqttServerConfiguration { Mapper = mapper },
            NullLogger<MqttSubjectServer>.Instance);

        var registeredLeaf = leaf.TryGetRegisteredSubject()!;
        _mapped = registeredLeaf.TryGetProperty(nameof(MqttBenchmarkNode.Name))!;
        _unmapped = registeredLeaf.TryGetProperty(nameof(MqttBenchmarkNode.Comment))!.Reference;
    }

    [GlobalCleanup]
    public void Cleanup() => _server.DisposeAsync().AsTask().GetAwaiter().GetResult();

    [Benchmark]
    public bool PublishedChange()
        => _server.IsPropertyIncluded(_mapped.Reference) && _server.TryGetTopicForProperty(_mapped).Topic is not null;

    [Benchmark]
    public bool UnmappedChange() => _server.IsPropertyIncluded(_unmapped);
}

[InterceptorSubject]
public partial class MqttBenchmarkNode
{
    [Path("mqtt", "name")]
    public partial string Name { get; set; }

    [Path("mqtt", "child")]
    public partial MqttBenchmarkNode? Child { get; set; }

    public partial string? Comment { get; set; }

    public MqttBenchmarkNode()
    {
        Name = string.Empty;
    }
}
