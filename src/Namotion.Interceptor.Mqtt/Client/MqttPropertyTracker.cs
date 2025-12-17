using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Sources;

namespace Namotion.Interceptor.Mqtt.Client;

/// <summary>
/// Property tracker for MQTT client sources.
/// Extends <see cref="SourcePropertyTracker"/> with MQTT-specific cache cleanup behavior.
/// </summary>
internal sealed class MqttPropertyTracker : SourcePropertyTracker
{
    private readonly ConcurrentDictionary<string, PropertyReference?> _topicToProperty;
    private readonly ConcurrentDictionary<PropertyReference, string?> _propertyToTopic;

    public MqttPropertyTracker(
        ISubjectSource source,
        ConcurrentDictionary<string, PropertyReference?> topicToProperty,
        ConcurrentDictionary<PropertyReference, string?> propertyToTopic,
        ILogger? logger = null)
        : base(source, logger)
    {
        _topicToProperty = topicToProperty;
        _propertyToTopic = propertyToTopic;
    }

    /// <inheritdoc />
    protected override void OnSubjectDetaching(IInterceptorSubject subject)
    {
        // Clean up topic caches for the detached subject
        // TODO(perf): O(n) scan over all cached entries per detached subject.
        // Consider adding a reverse index for O(1) cleanup if profiling shows this as a bottleneck.
        foreach (var kvp in _propertyToTopic)
        {
            if (kvp.Key.Subject == subject)
            {
                _propertyToTopic.TryRemove(kvp.Key, out _);
            }
        }

        foreach (var kvp in _topicToProperty)
        {
            if (kvp.Value?.Subject == subject)
            {
                _topicToProperty.TryRemove(kvp.Key, out _);
            }
        }
    }
}
