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
        var original = new HelloPayload { Version = 1, Format = WebSocketFormat.Json };

        var bytes = _serializer.Serialize(original);
        var deserialized = _serializer.Deserialize<HelloPayload>(bytes);

        Assert.Equal(original.Version, deserialized.Version);
        Assert.Equal(original.Format, deserialized.Format);
    }

    [Fact]
    public void SerializeAndDeserialize_SubjectUpdate_ShouldRoundTrip()
    {
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

        var bytes = _serializer.Serialize(original);
        var deserialized = _serializer.Deserialize<SubjectUpdate>(bytes);

        Assert.Equal("1", deserialized.Root);
        Assert.True(deserialized.Subjects.ContainsKey("1"));
        Assert.True(deserialized.Subjects["1"].ContainsKey("Temperature"));
    }

    [Fact]
    public void SerializeMessage_ShouldCreateEnvelopeArray()
    {
        var payload = new HelloPayload { Version = 1, Format = WebSocketFormat.Json };

        var bytes = _serializer.SerializeMessage(MessageType.Hello, null, payload);
        var json = System.Text.Encoding.UTF8.GetString(bytes);

        Assert.StartsWith("[0,null,", json); // [MessageType.Hello, null, payload]
    }

    [Fact]
    public void DeserializeMessageEnvelope_ShouldExtractComponents()
    {
        var payload = new HelloPayload { Version = 1, Format = WebSocketFormat.Json };
        var bytes = _serializer.SerializeMessage(MessageType.Hello, 42, payload);

        var (messageType, sequence, payloadStart, payloadLength) = _serializer.DeserializeMessageEnvelope(bytes);

        Assert.Equal(MessageType.Hello, messageType);
        Assert.Equal(42, sequence);

        var deserializedPayload = _serializer.Deserialize<HelloPayload>(bytes.AsSpan(payloadStart, payloadLength));
        Assert.Equal(1, deserializedPayload.Version);
    }

    [Fact]
    public void WelcomePayload_WithSubjectUpdate_ShouldRoundTrip()
    {
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

        var bytes = _serializer.SerializeMessage(MessageType.Welcome, null, welcome);
        var (messageType, _, payloadStart, payloadLength) = _serializer.DeserializeMessageEnvelope(bytes);

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

        var bytes = _serializer.Serialize(update);
        var deserialized = _serializer.Deserialize<SubjectUpdate>(bytes);

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
        Assert.ThrowsAny<JsonException>(() =>
            _serializer.DeserializeMessageEnvelope(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void DeserializeMessageEnvelope_WithNonArrayInput_ShouldThrow()
    {
        var bytes = Encoding.UTF8.GetBytes("{}");
        Assert.Throws<InvalidOperationException>(() =>
            _serializer.DeserializeMessageEnvelope(bytes));
    }

    [Fact]
    public void DeserializeMessageEnvelope_WithEmptyArray_ShouldThrow()
    {
        var bytes = Encoding.UTF8.GetBytes("[]");
        Assert.Throws<InvalidOperationException>(() =>
            _serializer.DeserializeMessageEnvelope(bytes));
    }

    [Fact]
    public void DeserializeMessageEnvelope_WithStringMessageType_ShouldThrow()
    {
        var bytes = Encoding.UTF8.GetBytes("[\"hello\",null,{}]");
        Assert.Throws<InvalidOperationException>(() =>
            _serializer.DeserializeMessageEnvelope(bytes));
    }

    [Fact]
    public void DeserializeMessageEnvelope_WithStringSequence_ShouldThrow()
    {
        var bytes = Encoding.UTF8.GetBytes("[0,\"bad\",{}]");
        Assert.Throws<InvalidOperationException>(() =>
            _serializer.DeserializeMessageEnvelope(bytes));
    }

    [Fact]
    public void DeserializeMessageEnvelope_WithMissingSequenceAndPayload_ShouldThrow()
    {
        // [0] has only the messageType; after reading it, the next Read()
        // returns EndArray which is neither null nor a number for sequence.
        var bytes = Encoding.UTF8.GetBytes("[0]");
        Assert.Throws<InvalidOperationException>(() =>
            _serializer.DeserializeMessageEnvelope(bytes));
    }

    [Fact]
    public void DeserializeMessageEnvelope_WithNullSequence_ShouldReturnNull()
    {
        var payload = new HelloPayload { Version = 1, Format = WebSocketFormat.Json };
        var bytes = _serializer.SerializeMessage(MessageType.Hello, null, payload);
        var (_, sequence, _, _) = _serializer.DeserializeMessageEnvelope(bytes);
        Assert.Null(sequence);
    }

    [Fact]
    public void Deserialize_WithNullJson_ShouldThrow()
    {
        var bytes = Encoding.UTF8.GetBytes("null");
        Assert.Throws<InvalidOperationException>(() =>
            _serializer.Deserialize<HelloPayload>(bytes));
    }

    [Fact]
    public void SerializeMessageTo_ShouldMatchSerializeMessage()
    {
        var payload = new HelloPayload { Version = 1, Format = WebSocketFormat.Json };
        var bytes = _serializer.SerializeMessage(MessageType.Hello, 42, payload);
        var bufferWriter = new ArrayBufferWriter<byte>(256);
        _serializer.SerializeMessageTo(bufferWriter, MessageType.Hello, 42, payload);
        Assert.Equal(bytes, bufferWriter.WrittenSpan.ToArray());
    }

    [Fact]
    public void SerializeAndDeserialize_ErrorPayload_WithFailures_ShouldRoundTrip()
    {
        var original = new ErrorPayload
        {
            Code = ErrorCode.ValidationFailed,
            Message = "Validation failed",
            Failures =
            [
                new PropertyFailure { Path = "Motor/Speed", Code = ErrorCode.ReadOnlyProperty, Message = "Read-only" }
            ]
        };

        var bytes = _serializer.SerializeMessage(MessageType.Error, null, original);
        var (messageType, _, payloadStart, payloadLength) = _serializer.DeserializeMessageEnvelope(bytes);

        Assert.Equal(MessageType.Error, messageType);

        var deserialized = _serializer.Deserialize<ErrorPayload>(bytes.AsSpan(payloadStart, payloadLength));
        Assert.Equal(ErrorCode.ValidationFailed, deserialized.Code);
        Assert.Equal("Validation failed", deserialized.Message);
        Assert.Single(deserialized.Failures!);
        Assert.Equal("Motor/Speed", deserialized.Failures![0].Path);
    }
}
