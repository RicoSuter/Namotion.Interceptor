using System;
using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Namotion.Interceptor.Mqtt;

/// <summary>
/// JSON-based MQTT value converter using System.Text.Json for high performance.
/// </summary>
public sealed class JsonMqttValueConverter : IMqttValueConverter
{
    private readonly JsonSerializerOptions _options;

    /// <summary>
    /// Initializes a new instance with default options.
    /// </summary>
    public JsonMqttValueConverter()
        : this(CreateDefaultOptions())
    {
    }

    /// <summary>
    /// Initializes a new instance with custom options.
    /// </summary>
    /// <param name="options">The JSON serializer options.</param>
    public JsonMqttValueConverter(JsonSerializerOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public byte[] Serialize(object? value, Type type)
    {
        return JsonSerializer.SerializeToUtf8Bytes(value, type, _options);
    }

    /// <inheritdoc />
    public object? Deserialize(ReadOnlySequence<byte> payload, Type type)
    {
        if (payload.IsEmpty)
        {
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }

        // Single segment - no copy needed (most common case)
        if (payload.IsSingleSegment)
        {
            return JsonSerializer.Deserialize(payload.FirstSpan, type, _options);
        }

        // Multi-segment - need to copy to contiguous buffer
        var length = (int)payload.Length;
        var buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            payload.CopyTo(buffer);
            return JsonSerializer.Deserialize(buffer.AsSpan(0, length), type, _options);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static JsonSerializerOptions CreateDefaultOptions()
    {
        return new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            Converters = { new JsonStringEnumConverter() }
        };
    }
}
