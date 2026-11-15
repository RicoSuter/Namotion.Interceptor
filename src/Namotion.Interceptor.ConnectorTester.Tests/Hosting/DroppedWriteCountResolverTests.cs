using Xunit;
using Namotion.Interceptor.ConnectorTester.Hosting;
using Namotion.Interceptor.ConnectorTester.Model;
using Namotion.Interceptor.ConnectorTester.Tests.Engine.Mutation;

namespace Namotion.Interceptor.ConnectorTester.Tests.Hosting;

public class DroppedWriteCountResolverTests
{
    [Fact]
    public void WhenAClientHasASourceForItsRoot_ThenTheCountReadsThatSourcesDroppedRetries()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var root = new TestNode(context);
        var otherSource = new FailingSource { RootSubject = new TestNode(context) };
        var source = new FailingSource { RootSubject = root };
        source.Metrics.OutboundRetries.AddDropped(3);

        // Act
        var droppedWriteCount = DroppedWriteCountResolver.Resolve([otherSource, source], root, "client-a", isServer: false);

        // Assert
        Assert.NotNull(droppedWriteCount);
        Assert.Equal(3, droppedWriteCount());
    }

    [Fact]
    public void WhenAClientHasNoSourceForItsRoot_ThenResolveThrows()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var root = new TestNode(context);
        var otherSource = new FailingSource { RootSubject = new TestNode(context) };

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(
            () => DroppedWriteCountResolver.Resolve([otherSource], root, "client-a", isServer: false));
        Assert.Contains("'client-a'", exception.Message);
    }

    [Fact]
    public void WhenTheParticipantIsTheServer_ThenThereIsNoCount()
    {
        // Arrange
        var root = new TestNode(InterceptorSubjectContext.Create());

        // Act
        var droppedWriteCount = DroppedWriteCountResolver.Resolve([], root, "server", isServer: true);

        // Assert
        Assert.Null(droppedWriteCount);
    }
}
