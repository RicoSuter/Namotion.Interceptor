using Namotion.Interceptor.Connectors.TwinCAT.Client;
using Xunit;

namespace Namotion.Interceptor.Connectors.TwinCAT.Tests.Client;

public class AdsWriteExceptionTests
{
    [Fact]
    public void Constructor_ShouldSetProperties()
    {
        var exception = new AdsWriteException(3, 2, 10);

        Assert.Equal(3, exception.TransientCount);
        Assert.Equal(2, exception.PermanentCount);
        Assert.Equal(10, exception.TotalCount);
    }

    [Fact]
    public void Constructor_ShouldFormatMessage()
    {
        var exception = new AdsWriteException(3, 2, 10);

        Assert.Contains("3 transient", exception.Message);
        Assert.Contains("2 permanent", exception.Message);
        Assert.Contains("10 total", exception.Message);
    }

    [Fact]
    public void Constructor_WithZeroCounts_ShouldWork()
    {
        var exception = new AdsWriteException(0, 0, 0);

        Assert.Equal(0, exception.TransientCount);
        Assert.Equal(0, exception.PermanentCount);
        Assert.Equal(0, exception.TotalCount);
    }
}
