using Xunit;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.ConnectorTester.Snapshot;

namespace Namotion.Interceptor.ConnectorTester.Tests.Snapshot;

public class SnapshotIdMapTests
{
    [Fact]
    public void WhenRootHasNoChildren_ThenOnlyRootIsMapped()
    {
        // Arrange
        var update = new SubjectUpdate
        {
            Root = "raw-root",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["raw-root"] = new()
            }
        };

        // Act
        var idMap = SnapshotIdMap.Build(update);

        // Assert
        Assert.Single(idMap);
        Assert.Equal("ROOT", idMap["raw-root"]);
    }

    [Fact]
    public void WhenObjectChildPresent_ThenChildGetsSubj1()
    {
        // Arrange
        var update = new SubjectUpdate
        {
            Root = "raw-root",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["raw-root"] = new()
                {
                    ["Ref"] = new SubjectPropertyUpdate { Kind = SubjectPropertyUpdateKind.Object, Id = "raw-child" }
                },
                ["raw-child"] = new()
            }
        };

        // Act
        var idMap = SnapshotIdMap.Build(update);

        // Assert
        Assert.Equal("ROOT", idMap["raw-root"]);
        Assert.Equal("SUBJ_1", idMap["raw-child"]);
    }
}
