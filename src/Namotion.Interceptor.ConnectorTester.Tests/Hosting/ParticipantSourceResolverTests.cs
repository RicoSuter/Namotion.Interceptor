using Xunit;
using Namotion.Interceptor.ConnectorTester.Hosting;
using Namotion.Interceptor.ConnectorTester.Model;
using Namotion.Interceptor.ConnectorTester.Tests.Engine.Mutation;

namespace Namotion.Interceptor.ConnectorTester.Tests.Hosting;

public class ParticipantSourceResolverTests
{
    [Fact]
    public void WhenAClientHasASourceForItsRoot_ThenResolveReturnsThatSource()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var root = new TestNode(context);
        var otherSource = new FailingSource { RootSubject = new TestNode(context) };
        var source = new FailingSource { RootSubject = root };

        // Act
        var resolvedSource = ParticipantSourceResolver.Resolve([otherSource, source], root, "client-a", isServer: false);

        // Assert
        Assert.Same(source, resolvedSource);
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
            () => ParticipantSourceResolver.Resolve([otherSource], root, "client-a", isServer: false));
        Assert.Contains("'client-a'", exception.Message);
    }

    [Fact]
    public void WhenTheParticipantIsTheServer_ThenThereIsNoSource()
    {
        // Arrange
        var root = new TestNode(InterceptorSubjectContext.Create());

        // Act
        var source = ParticipantSourceResolver.Resolve([], root, "server", isServer: true);

        // Assert
        Assert.Null(source);
    }
}
