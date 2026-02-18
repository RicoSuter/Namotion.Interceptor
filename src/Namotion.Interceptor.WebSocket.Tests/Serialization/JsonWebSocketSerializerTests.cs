using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.WebSocket.Protocol;
using Namotion.Interceptor.WebSocket.Serialization;
using Xunit;

namespace Namotion.Interceptor.WebSocket.Tests.Serialization;

public class JsonWebSocketSerializerTests
{
    private readonly JsonWebSocketSerializer _serializer = new();

    [Fact]
    public void SerializeAndDeserialize_HelloPayload_ShouldRoundTrip()
    {
        // Arrange
        var original = new HelloPayload { Version = 1, Format = WebSocketFormat.Json };

        // Act
        var bytes = _serializer.Serialize(original);
        var deserialized = _serializer.Deserialize<HelloPayload>(bytes);

        // Assert
        Assert.Equal(original.Version, deserialized.Version);
        Assert.Equal(original.Format, deserialized.Format);
    }

    [Fact]
    public void SerializeAndDeserialize_SubjectUpdate_ShouldRoundTrip()
    {
        // Arrange
        var original = new SubjectUpdate
        {
            Root = "1",
            Subjects =
            {
                ["1"] = new Dictionary<string, SubjectPropertyUpdate>
                {
                    ["Temperature"] = new SubjectPropertyUpdate { Kind = SubjectPropertyUpdateKind.Value, Value = 23.5 }
                }
            }
        };

        // Act
        var bytes = _serializer.Serialize(original);
        var deserialized = _serializer.Deserialize<SubjectUpdate>(bytes);

        // Assert
        Assert.Equal("1", deserialized.Root);
        Assert.True(deserialized.Subjects.ContainsKey("1"));
        Assert.True(deserialized.Subjects["1"].ContainsKey("Temperature"));
    }

    [Fact]
    public void SerializeMessage_ShouldCreateEnvelopeArray()
    {
        // Arrange
        var payload = new HelloPayload { Version = 1, Format = WebSocketFormat.Json };

        // Act
        var bytes = _serializer.SerializeMessage(MessageType.Hello, payload);
        var json = System.Text.Encoding.UTF8.GetString(bytes);

        // Assert
        Assert.StartsWith("[0,", json); // [MessageType, payload]
        Assert.DoesNotContain("null", json); // no null sequence field
    }

    [Fact]
    public void DeserializeMessageEnvelope_ShouldExtractComponents()
    {
        // Arrange
        var payload = new HelloPayload { Version = 1, Format = WebSocketFormat.Json };
        var bytes = _serializer.SerializeMessage(MessageType.Hello, payload);

        // Act
        var (messageType, payloadStart, payloadLength) = _serializer.DeserializeMessageEnvelope(bytes);

        // Assert
        Assert.Equal(MessageType.Hello, messageType);

        var deserializedPayload = _serializer.Deserialize<HelloPayload>(bytes.AsSpan(payloadStart, payloadLength));
        Assert.Equal(1, deserializedPayload.Version);
    }

    [Fact]
    public void WelcomePayload_WithSubjectUpdate_ShouldRoundTrip()
    {
        // Arrange
        var update = new SubjectUpdate
        {
            Root = "1",
            Subjects =
            {
                ["1"] = new Dictionary<string, SubjectPropertyUpdate>
                {
                    ["Name"] = new SubjectPropertyUpdate { Kind = SubjectPropertyUpdateKind.Value, Value = "TestName" },
                    ["Number"] = new SubjectPropertyUpdate { Kind = SubjectPropertyUpdateKind.Value, Value = 42.5m }
                }
            }
        };

        var welcome = new WelcomePayload
        {
            Version = 1,
            Format = WebSocketFormat.Json,
            State = update
        };

        // Act
        var bytes = _serializer.SerializeMessage(MessageType.Welcome, welcome);
        var (messageType, payloadStart, payloadLength) = _serializer.DeserializeMessageEnvelope(bytes);

        // Assert
        Assert.Equal(MessageType.Welcome, messageType);

        var deserializedWelcome = _serializer.Deserialize<WelcomePayload>(bytes.AsSpan(payloadStart, payloadLength));
        Assert.NotNull(deserializedWelcome.State);
        Assert.True(deserializedWelcome.State.Subjects.ContainsKey("1"));
        Assert.True(deserializedWelcome.State.Subjects["1"].ContainsKey("Name"));

        var nameUpdate = deserializedWelcome.State.Subjects["1"]["Name"];
        Assert.Equal("TestName", nameUpdate.Value?.ToString());
    }

    [Fact]
    public void SubjectPropertyUpdate_Value_IsJsonElementAfterDeserialization()
    {
        // This test documents that Value is JsonElement after deserialization
        // because System.Text.Json doesn't know the target type for object?.
        // The conversion to the correct type happens in ApplySubjectUpdate
        // which uses the property's declared type from the registry.

        // Arrange
        var update = new SubjectUpdate
        {
            Root = "1",
            Subjects =
            {
                ["1"] = new Dictionary<string, SubjectPropertyUpdate>
                {
                    ["Name"] = new SubjectPropertyUpdate { Kind = SubjectPropertyUpdateKind.Value, Value = "TestValue" }
                }
            }
        };

        // Act
        var bytes = _serializer.Serialize(update);
        var deserialized = _serializer.Deserialize<SubjectUpdate>(bytes);

        // Assert
        var nameUpdate = deserialized.Subjects["1"]["Name"];
        Assert.NotNull(nameUpdate.Value);

        // Value is JsonElement after deserialization - this is expected behavior
        Assert.Equal(typeof(System.Text.Json.JsonElement), nameUpdate.Value.GetType());

        // But the string value can still be extracted
        var jsonElement = (System.Text.Json.JsonElement)nameUpdate.Value;
        Assert.Equal("TestValue", jsonElement.GetString());
    }

    [Fact]
    public void DeserializeMessageEnvelope_WithEmptyInput_ShouldThrow()
    {
        // Utf8JsonReader.Read() throws a JsonException subclass on empty input
        // before our validation code can check the return value.

        // Act & Assert
        Assert.ThrowsAny<JsonException>(() =>
            _serializer.DeserializeMessageEnvelope(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void DeserializeMessageEnvelope_WithNonArrayInput_ShouldThrow()
    {
        // Arrange
        var bytes = Encoding.UTF8.GetBytes("{}");

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() =>
            _serializer.DeserializeMessageEnvelope(bytes));
    }

    [Fact]
    public void DeserializeMessageEnvelope_WithEmptyArray_ShouldThrow()
    {
        // Arrange
        var bytes = Encoding.UTF8.GetBytes("[]");

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() =>
            _serializer.DeserializeMessageEnvelope(bytes));
    }

    [Fact]
    public void DeserializeMessageEnvelope_WithStringMessageType_ShouldThrow()
    {
        // Arrange
        var bytes = Encoding.UTF8.GetBytes("[\"hello\",{}]");

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() =>
            _serializer.DeserializeMessageEnvelope(bytes));
    }

    [Fact]
    public void DeserializeMessageEnvelope_WithMissingPayload_ShouldThrow()
    {
        // Arrange
        var bytes = Encoding.UTF8.GetBytes("[0]");

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() =>
            _serializer.DeserializeMessageEnvelope(bytes));
    }

    [Fact]
    public void Deserialize_WithNullJson_ShouldThrow()
    {
        // Arrange
        var bytes = Encoding.UTF8.GetBytes("null");

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() =>
            _serializer.Deserialize<HelloPayload>(bytes));
    }

    [Fact]
    public void SerializeMessageTo_ShouldMatchSerializeMessage()
    {
        // Arrange
        var payload = new HelloPayload { Version = 1, Format = WebSocketFormat.Json };
        var bufferWriter = new ArrayBufferWriter<byte>(256);

        // Act
        var bytes = _serializer.SerializeMessage(MessageType.Hello, payload);
        _serializer.SerializeMessageTo(bufferWriter, MessageType.Hello, payload);

        // Assert
        Assert.Equal(bytes, bufferWriter.WrittenSpan.ToArray());
    }

    [Fact]
    public void SerializeAndDeserialize_ErrorPayload_WithFailures_ShouldRoundTrip()
    {
        // Arrange
        var original = new ErrorPayload
        {
            Code = ErrorCode.ValidationFailed,
            Message = "Validation failed",
            Failures =
            [
                new PropertyFailure { Path = "Motor/Speed", Code = ErrorCode.ReadOnlyProperty, Message = "Read-only" }
            ]
        };

        // Act
        var bytes = _serializer.SerializeMessage(MessageType.Error, original);
        var (messageType, payloadStart, payloadLength) = _serializer.DeserializeMessageEnvelope(bytes);

        // Assert
        Assert.Equal(MessageType.Error, messageType);

        var deserialized = _serializer.Deserialize<ErrorPayload>(bytes.AsSpan(payloadStart, payloadLength));
        Assert.Equal(ErrorCode.ValidationFailed, deserialized.Code);
        Assert.Equal("Validation failed", deserialized.Message);
        Assert.Single(deserialized.Failures!);
        Assert.Equal("Motor/Speed", deserialized.Failures![0].Path);
    }
}
