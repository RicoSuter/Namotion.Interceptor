namespace Namotion.Interceptor.OpcUa.Tests.Formal;

public class ModelTraceTests
{
    [Fact]
    public void WhenSinkInstalled_ThenSetForwardsFieldAndValue()
    {
        // Arrange
        var captured = new List<(string field, string? key, string value)>();
        Namotion.Interceptor.Diagnostics.ModelTrace.Sink.Value = (f, k, v) => captured.Add((f, k, v));

        // Act
        Namotion.Interceptor.Diagnostics.ModelTrace.Set("state", "SessionActive");
        Namotion.Interceptor.Diagnostics.ModelTrace.SetItem("cover", "ns=2;s=A", "Subscribed");

        // Assert
        Assert.Equal(("state", null, "SessionActive"), captured[0]);
        Assert.Equal(("cover", "ns=2;s=A", "Subscribed"), captured[1]);
        Namotion.Interceptor.Diagnostics.ModelTrace.Sink.Value = null;
    }
}
