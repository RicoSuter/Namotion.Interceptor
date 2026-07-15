using System.Collections.Generic;
using Namotion.Interceptor.Diagnostics;
using Xunit;

namespace Namotion.Interceptor.OpcUa.Tests.Formal;

public class ModelTraceTests
{
    private sealed class RecordingSink : IModelTraceSink
    {
        public readonly List<(string field, string? key, string value)> Records = new();
        public int Commits;
        public void Record(string field, string? key, string value) => Records.Add((field, key, value));
        public void Commit() => Commits++;
    }

    [Fact]
    public void WhenSinkInstalled_ThenSetSetItemAndCommitAreForwarded()
    {
        // Arrange
        var sink = new RecordingSink();
        ModelTrace.Sink.Value = sink;

        // Act
        ModelTrace.Set("state", "SessionActive");
        ModelTrace.SetItem("cover", "ns=2;s=A", "Subscribed");
        ModelTrace.Commit();

        // Assert
        Assert.Equal(("state", (string?)null, "SessionActive"), sink.Records[0]);
        Assert.Equal(("cover", "ns=2;s=A", "Subscribed"), sink.Records[1]);
        Assert.Equal(1, sink.Commits);
        ModelTrace.Sink.Value = null;
    }
}
