using System.Diagnostics.CodeAnalysis;
using Namotion.Interceptor.Mcp.Tools;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using static Namotion.Interceptor.Mcp.Tests.TestGraphs;

namespace Namotion.Interceptor.Mcp.Tests.Tools.Access;

public class PathProviderVersionTests
{
    [Fact]
    public void WhenTheInnerProviderRaisesItsVersion_ThenTheAccessPathProviderReturnsIt()
    {
        // Arrange
        var inner = new VersionedPathProvider();
        var provider = new McpAccessPathProvider(inner, new TestAccessPolicy((_, _) => true));

        // Act
        inner.CurrentVersion = 3;

        // Assert
        Assert.Equal(3, provider.Version);
    }

    [Fact]
    public void WhenTheInnerProviderRaisesItsVersion_ThenTheReadableSubjectsPathProviderReturnsIt()
    {
        // Arrange
        var inner = new VersionedPathProvider();
        var configuration = new McpServerConfiguration
        {
            PathProvider = inner,
            AccessPolicy = new TestAccessPolicy((_, _) => true)
        };
        var room = new TestRoom(CreateContext()) { Name = "Kitchen" };
        var provider = McpSubjectReadability.For(configuration, room).GetReadableSubjectsPathProvider(inner);

        // Act
        inner.CurrentVersion = 3;

        // Assert
        Assert.Equal(3, provider.Version);
    }

    private sealed class VersionedPathProvider : PathProviderBase
    {
        public long CurrentVersion { get; set; }

        public override long Version => CurrentVersion;

        public override bool TryGetPropertySegment(RegisteredSubjectProperty property, [NotNullWhen(true)] out string? segment)
        {
            segment = property.BrowseName;
            return true;
        }
    }
}
