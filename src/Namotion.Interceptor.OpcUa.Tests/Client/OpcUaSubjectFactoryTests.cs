using Moq;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Dynamic;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

public class OpcUaSubjectFactoryTests
{
    [Fact]
    public async Task WhenCreateCollectionSubjectAsync_ThenDelegatesToSubjectFactory()
    {
        // Arrange
        var expectedSubject = new DynamicSubject(InterceptorSubjectContext.Create().WithRegistry());
        var mockSubjectFactory = new Mock<ISubjectFactory>();
        mockSubjectFactory
            .Setup(f => f.CreateSubject(It.IsAny<Type>(), It.IsAny<IServiceProvider?>()))
            .Returns(expectedSubject);

        var opcUaSubjectFactory = new OpcUaSubjectFactory(mockSubjectFactory.Object);

        var context = InterceptorSubjectContext.Create().WithRegistry();
        var parentSubject = new DynamicSubject(context);
        var registeredSubject = parentSubject.TryGetRegisteredSubject()!;
        var collectionProperty = registeredSubject.AddProperty(
            "Items",
            typeof(DynamicSubject[]),
            _ => null,
            (_, _) => { });

        var nodeReference = new ReferenceDescription
        {
            BrowseName = new QualifiedName("Item"),
            NodeId = new ExpandedNodeId(new NodeId(100, 2)),
            NodeClass = NodeClass.Object
        };

        var mockSession = new Mock<ISession>();

        // Act
        var result = await opcUaSubjectFactory.CreateCollectionSubjectAsync(
            collectionProperty, nodeReference, 0, mockSession.Object, CancellationToken.None);

        // Assert
        Assert.Same(expectedSubject, result);
        mockSubjectFactory.Verify(
            f => f.CreateSubject(It.IsAny<Type>(), It.IsAny<IServiceProvider?>()),
            Times.Once);
    }
}
