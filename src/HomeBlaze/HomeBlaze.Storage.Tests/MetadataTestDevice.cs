using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Storage.Tests;

/// <summary>A configurable subject with subjects inside it, for the metadata of subjects and properties in its file.</summary>
[InterceptorSubject]
public partial class MetadataTestDevice : IConfigurable
{
    [Configuration]
    public partial string Name { get; set; }

    [State]
    public partial string? Firmware { get; set; }

    [Configuration]
    public partial MetadataTestChannel? Channel { get; set; }

    [Configuration]
    public partial MetadataTestChannel[] Channels { get; set; }

    [Configuration]
    public partial Dictionary<string, MetadataTestChannel> Meters { get; set; }

    public MetadataTestDevice()
    {
        Name = string.Empty;
        Channels = [];
        Meters = new Dictionary<string, MetadataTestChannel>();
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>The file of a device with a channel, <paramref name="channelCount"/> channels and a meter named Main.</summary>
    public static string Serialize(int channelCount = 2)
        => $$"""
            {
              "$type": "{{typeof(MetadataTestDevice).FullName}}",
              "name": "Shelly",
              "channel": {{MetadataTestChannel.Serialize("Pump")}},
              "channels": [{{string.Join(", ", Enumerable.Range(0, channelCount).Select(index => MetadataTestChannel.Serialize($"Channel {index}")))}}],
              "meters": { "Main": {{MetadataTestChannel.Serialize("Main")}} }
            }
            """;
}

[InterceptorSubject]
public partial class MetadataTestChannel : IConfigurable
{
    [Configuration]
    public partial string Name { get; set; }

    [State]
    public partial double Power { get; set; }

    public MetadataTestChannel()
    {
        Name = string.Empty;
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public static string Serialize(string name)
        => $$"""{ "$type": "{{typeof(MetadataTestChannel).FullName}}", "name": "{{name}}" }""";
}
