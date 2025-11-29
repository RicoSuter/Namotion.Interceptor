using System.Buffers;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Namotion.Interceptor.Mqtt.Tests;

public class JsonMqttValueConverterTests
{
    private readonly JsonMqttValueConverter _converter = new();

    [Fact]
    public void Serialize_NullValue_ReturnsJsonNull()
    {
        // Act
        var result = _converter.Serialize(null, typeof(object));

        // Assert
        Assert.Equal("null", Encoding.UTF8.GetString(result));
    }

    [Theory]
    [InlineData(42, typeof(int), "42")]
    [InlineData(3.14, typeof(double), "3.14")]
    [InlineData(true, typeof(bool), "true")]
    [InlineData(false, typeof(bool), "false")]
    [InlineData("test", typeof(string), "\"test\"")]
    public void Serialize_PrimitiveTypes_ReturnsCorrectJson(object value, Type type, string expected)
    {
        // Act
        var result = _converter.Serialize(value, type);

        // Assert
        Assert.Equal(expected, Encoding.UTF8.GetString(result));
    }

    [Fact]
    public void Serialize_ComplexObject_ReturnsCorrectJson()
    {
        // Arrange
        var obj = new TestObject { Name = "Test", Value = 123 };

        // Act
        var result = _converter.Serialize(obj, typeof(TestObject));

        // Assert
        var json = Encoding.UTF8.GetString(result);
        Assert.Contains("\"name\":", json); // camelCase
        Assert.Contains("\"Test\"", json);
        Assert.Contains("\"value\":", json);
        Assert.Contains("123", json);
    }

    [Fact]
    public void Deserialize_EmptyPayload_ReturnsNull()
    {
        // Arrange
        var payload = new ReadOnlySequence<byte>(Array.Empty<byte>());

        // Act
        var result = _converter.Deserialize(payload, typeof(string));

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void Deserialize_JsonNull_ReturnsNull()
    {
        // Arrange
        var payload = new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes("null"));

        // Act
        var result = _converter.Deserialize(payload, typeof(string));

        // Assert
        Assert.Null(result);
    }

    [Theory]
    [InlineData("42", typeof(int), 42)]
    [InlineData("3.14", typeof(double), 3.14)]
    [InlineData("true", typeof(bool), true)]
    [InlineData("\"test\"", typeof(string), "test")]
    public void Deserialize_PrimitiveTypes_ReturnsCorrectValue(string json, Type type, object expected)
    {
        // Arrange
        var payload = new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(json));

        // Act
        var result = _converter.Deserialize(payload, type);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Deserialize_ComplexObject_ReturnsCorrectObject()
    {
        // Arrange
        var json = "{\"name\":\"Test\",\"value\":123}";
        var payload = new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(json));

        // Act
        var result = _converter.Deserialize(payload, typeof(TestObject)) as TestObject;

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Test", result.Name);
        Assert.Equal(123, result.Value);
    }

    [Fact]
    public void Deserialize_InvalidJson_ThrowsJsonException()
    {
        // Arrange
        var payload = new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes("invalid json"));

        // Act & Assert
        Assert.Throws<JsonException>(() => _converter.Deserialize(payload, typeof(int)));
    }

    [Fact]
    public void RoundTrip_PreservesValue()
    {
        // Arrange
        var original = new TestObject { Name = "RoundTrip", Value = 456 };

        // Act
        var serialized = _converter.Serialize(original, typeof(TestObject));
        var deserialized = _converter.Deserialize(new ReadOnlySequence<byte>(serialized), typeof(TestObject)) as TestObject;

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(original.Name, deserialized.Name);
        Assert.Equal(original.Value, deserialized.Value);
    }

    private class TestObject
    {
        public string Name { get; set; } = string.Empty;
        public int Value { get; set; }
    }
}
