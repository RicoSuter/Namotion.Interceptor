using System;
using System.Collections.Generic;
using MQTTnet.Packets;

namespace Namotion.Interceptor.Mqtt;

internal static class MqttHelper
{
    public static DateTimeOffset? ExtractSourceTimestamp(
        IReadOnlyCollection<MqttUserProperty>? userProperties,
        string? timestampPropertyName,
        Func<string, DateTimeOffset?> deserializer)
    {
        if (timestampPropertyName is null || userProperties is null)
        {
            return null;
        }

        foreach (var prop in userProperties)
        {
            if (prop.Name == timestampPropertyName)
            {
                return deserializer(prop.Value);
            }
        }

        return null;
    }

    public static string BuildTopic(string path, string? topicPrefix)
    {
        return topicPrefix is null
            ? path
            : string.Concat(topicPrefix, "/", path);
    }

    public static string StripTopicPrefix(string topic, string? topicPrefix)
    {
        if (topicPrefix is not null &&
            topic.StartsWith(topicPrefix, StringComparison.Ordinal) &&
            topic.Length > topicPrefix.Length &&
            topic[topicPrefix.Length] == '/')
        {
            return topic.Substring(topicPrefix.Length + 1);
        }

        return topic;
    }
}
