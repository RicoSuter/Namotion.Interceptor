using System.Reactive.Concurrency;
using Namotion.Interceptor.GraphQL;
using Namotion.Interceptor.GraphQL.Tests.Models;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Xunit;

namespace Namotion.Interceptor.GraphQL.Tests;

public class BufferingTests
{
    [Fact]
    public void BufferTime_WhenSetInConfiguration_IsRespected()
    {
        // Arrange
        var config = new GraphQLSubjectConfiguration
        {
            BufferTime = TimeSpan.FromMilliseconds(100)
        };

        // Assert
        Assert.Equal(TimeSpan.FromMilliseconds(100), config.BufferTime);
    }

    [Fact]
    public void DefaultBufferTime_Is50Milliseconds()
    {
        // Arrange
        var config = new GraphQLSubjectConfiguration();

        // Assert
        Assert.Equal(TimeSpan.FromMilliseconds(50), config.BufferTime);
    }
}
