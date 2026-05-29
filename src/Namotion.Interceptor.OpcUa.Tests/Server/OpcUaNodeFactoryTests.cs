using Namotion.Interceptor.OpcUa.Server;
using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Tests.Server;

public class OpcUaNodeFactoryTests
{
    [Fact]
    public void WhenBrowseNamespaceUriIsRegistered_ThenResolveBrowseNamespaceIndexReturnsIndex()
    {
        // Arrange
        var namespaceUris = new NamespaceTable();
        var expectedIndex = (ushort)namespaceUris.GetIndexOrAppend("http://example.com/UA/");

        // Act
        var index = OpcUaNodeFactory.ResolveBrowseNamespaceIndex(
            namespaceUris, "http://example.com/UA/", "MyNode");

        // Assert
        Assert.Equal(expectedIndex, index);
    }

    [Fact]
    public void WhenBrowseNamespaceUriIsNotRegistered_ThenResolveBrowseNamespaceIndexThrows()
    {
        // Arrange - the URI is never appended to the namespace table
        var namespaceUris = new NamespaceTable();

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() =>
            OpcUaNodeFactory.ResolveBrowseNamespaceIndex(
                namespaceUris, "http://unregistered.example/", "MyNode"));

        Assert.Contains("http://unregistered.example/", exception.Message);
        Assert.Contains("MyNode", exception.Message);
    }
}
