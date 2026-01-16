using Namotion.Interceptor.GraphQL;
using Namotion.Interceptor.Registry.Paths;
using Xunit;

namespace Namotion.Interceptor.GraphQL.Tests;

public class GraphQLSubjectConfigurationTests
{
    [Fact]
    public void DefaultConfiguration_HasExpectedDefaults()
    {
        // Act
        var config = new GraphQLSubjectConfiguration();

        // Assert
        Assert.Equal("root", config.RootName);
        Assert.Equal(TimeSpan.FromMilliseconds(50), config.BufferTime);
        Assert.IsType<CamelCasePathProvider>(config.PathProvider);
    }
}
