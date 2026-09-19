using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.WebSocket.Protocol;
using Xunit;

namespace Namotion.Interceptor.WebSocket.Tests.Protocol;

public class PayloadTests
{
    [Fact]
    public void HelloPayload_ShouldHaveDefaultValues()
    {
        // Act
        var payload = new HelloPayload();

        // Assert
        Assert.Equal(WebSocketProtocol.Version, payload.Version);
        Assert.Equal(WebSocketFormat.Json, payload.Format);
    }

    [Fact]
    public void WelcomePayload_ShouldHaveDefaultValues()
    {
        // Act
        var payload = new WelcomePayload();

        // Assert
        Assert.Equal(WebSocketProtocol.Version, payload.Version);
        Assert.Equal(WebSocketFormat.Json, payload.Format);
        Assert.Null(payload.State);
    }

    [Fact]
    public void WelcomePayload_ShouldDefaultSequenceToZero()
    {
        // Act
        var payload = new WelcomePayload();

        // Assert
        Assert.Equal(0L, payload.Sequence);
    }

    [Fact]
    public void HeartbeatPayload_ShouldHaveDefaultValues()
    {
        // Act
        var payload = new HeartbeatPayload();

        // Assert
        Assert.Equal(0L, payload.Sequence);

        // A newer client reading no value from an older server must behave as if nothing had been
        // applied, which is what the null default guarantees.
        Assert.Null(payload.AppliedThrough);
    }

    [Fact]
    public void MessageType_ShouldIncludeHeartbeat()
    {
        // Act & Assert
        Assert.Equal(4, (int)MessageType.Heartbeat);
    }

    [Fact]
    public void ErrorPayload_ShouldSupportMultipleFailures()
    {
        // Act
        var payload = new ErrorPayload
        {
            Code = 100,
            Message = "Multiple failures",
            Failures =
            [
                new PropertyFailure { Path = "Motor/Speed", Code = 101, Message = "Read-only" },
                new PropertyFailure { Path = "Sensor/Unknown", Code = 100, Message = "Not found" }
            ]
        };

        // Assert
        Assert.Equal(100, payload.Code);
        Assert.Equal(2, payload.Failures!.Count);
        Assert.Equal("Motor/Speed", payload.Failures[0].Path);
    }

    [Fact]
    public void UpdatePayload_ShouldInheritFromSubjectUpdate()
    {
        // Act
        var payload = new UpdatePayload
        {
            Sequence = 42,
            Root = "1",
            Subjects = { ["1"] = new Dictionary<string, SubjectPropertyUpdate>() }
        };

        // Assert
        Assert.Equal(42, payload.Sequence);
        Assert.Equal("1", payload.Root);
        Assert.IsAssignableFrom<SubjectUpdate>(payload);
    }

    [Fact]
    public void UpdatePayload_SequenceShouldDefaultToNull()
    {
        // Act
        var payload = new UpdatePayload();

        // Assert
        Assert.Null(payload.Sequence);
    }

    /// <summary>
    /// Guards the copy the server uses to stamp a sequence number onto an update. A field of
    /// <see cref="SubjectUpdate"/> that the copy misses is absent from every update the server sends,
    /// with no error anywhere, so this fails as soon as a new field is not carried over.
    /// </summary>
    [Fact]
    public void WhenUpdatePayloadIsBuiltFromAnUpdate_ThenEveryFieldOfTheUpdateIsCarriedOver()
    {
        // Arrange
        var updateProperties = typeof(SubjectUpdate)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanWrite)
            .ToArray();

        Assert.NotEmpty(updateProperties);

        var source = new SubjectUpdate();
        foreach (var property in updateProperties)
        {
            property.SetValue(source, CreateDistinctValue(property.PropertyType));
        }

        // Act
        var payload = new UpdatePayload(source);

        // Assert
        foreach (var property in updateProperties)
        {
            Assert.Equal(property.GetValue(source), property.GetValue(payload));
        }
    }

    private static object CreateDistinctValue(Type type)
    {
        if (type == typeof(string))
        {
            return "copy-guard";
        }

        if (type == typeof(HashSet<string>))
        {
            return new HashSet<string> { "copy-guard" };
        }

        if (type == typeof(Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>))
        {
            return new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["copy-guard"] = new() { ["Value"] = new SubjectPropertyUpdate() }
            };
        }

        if (!type.IsValueType && type.GetConstructor(Type.EmptyTypes) is { } constructor)
        {
            return constructor.Invoke(null);
        }

        Assert.Fail(
            $"SubjectUpdate gained a property of type {type}. Copy it in the SubjectUpdate copy " +
            "constructor and teach this method how to build a distinguishable value for it.");
        return null!;
    }
}
