using Namotion.Interceptor.OpcUa.Client;
using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

/// <summary>
/// Tests for matching a configured RootPath segment against browse results, which run on
/// raw references that are not filtered through DistinctByResolvedNodeId. A server may return
/// a reference with a missing BrowseName, so the match must tolerate null BrowseNames.
/// </summary>
public class OpcUaRootPathResolutionTests
{
    [Fact]
    public void WhenAReferenceHasNullBrowseName_ThenItIsSkippedAndTheMatchingChildIsFound()
    {
        // Arrange
        var references = new ReferenceDescriptionCollection
        {
            new ReferenceDescription { NodeId = new ExpandedNodeId(new NodeId(1, 0)) }, // null BrowseName
            new ReferenceDescription
            {
                BrowseName = new QualifiedName("Machines"),
                NodeId = new ExpandedNodeId(new NodeId(2, 0))
            }
        };

        // Act
        var match = OpcUaSubjectClientSource.FindChildByBrowseName(references, "Machines");

        // Assert
        Assert.NotNull(match);
        Assert.Equal("Machines", match.BrowseName.Name);
    }

    [Fact]
    public void WhenNoReferenceMatches_ThenReturnsNull()
    {
        // Arrange
        var references = new ReferenceDescriptionCollection
        {
            new ReferenceDescription { NodeId = new ExpandedNodeId(new NodeId(1, 0)) }, // null BrowseName
            new ReferenceDescription
            {
                BrowseName = new QualifiedName("Other"),
                NodeId = new ExpandedNodeId(new NodeId(2, 0))
            }
        };

        // Act
        var match = OpcUaSubjectClientSource.FindChildByBrowseName(references, "Machines");

        // Assert
        Assert.Null(match);
    }
}
