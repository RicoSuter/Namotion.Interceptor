using Namotion.Interceptor.OpcUa.Server;
using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Tests.Server;

public class OpcUaNodeFactoryTests
{
    [Fact]
    public void WhenNamespaceUriIsRegistered_ThenResolveNamespaceIndexReturnsIndex()
    {
        // Arrange
        var namespaceUris = new NamespaceTable();
        var expectedIndex = (ushort)namespaceUris.GetIndexOrAppend("http://example.com/UA/");

        // Act
        var index = OpcUaNodeFactory.ResolveNamespaceIndex(
            namespaceUris, "http://example.com/UA/", "BrowseName namespace URI", "MyNode");

        // Assert
        Assert.Equal(expectedIndex, index);
    }

    [Fact]
    public void WhenNamespaceUriIsNotRegistered_ThenResolveNamespaceIndexThrows()
    {
        // Arrange - the URI is never appended to the namespace table
        var namespaceUris = new NamespaceTable();

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() =>
            OpcUaNodeFactory.ResolveNamespaceIndex(
                namespaceUris, "http://unregistered.example/", "BrowseName namespace URI", "MyNode"));

        Assert.Contains("http://unregistered.example/", exception.Message);
        Assert.Contains("MyNode", exception.Message);
        Assert.Contains("BrowseName namespace URI", exception.Message);
    }
}
