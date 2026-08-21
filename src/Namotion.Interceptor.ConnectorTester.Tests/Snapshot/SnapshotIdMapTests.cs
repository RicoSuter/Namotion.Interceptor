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

    [Fact]
    public void WhenSubjectIsReachableByTwoPaths_ThenItIsMappedOnceAndBuildTerminates()
    {
        // Arrange: raw-shared hangs off the root's collection and off the holder's object
        // reference, so the walk meets it twice.
        var update = new SubjectUpdate
        {
            Root = "raw-root",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["raw-root"] = new()
                {
                    ["Collection"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Collection,
                        Items =
                        [
                            new SubjectPropertyItemUpdate { Id = "raw-holder" },
                            new SubjectPropertyItemUpdate { Id = "raw-shared" }
                        ]
                    }
                },
                ["raw-holder"] = new()
                {
                    ["ObjectRef"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Object,
                        Id = "raw-shared"
                    }
                },
                ["raw-shared"] = new()
            }
        };

        // Act
        var idMap = SnapshotIdMap.Build(update);

        // Assert: three distinct subjects, the shared one carrying exactly one normalized id.
        Assert.Equal(3, idMap.Count);
        Assert.Equal("ROOT", idMap["raw-root"]);
        Assert.Equal("SUBJ_1", idMap["raw-holder"]);
        Assert.Equal("SUBJ_2", idMap["raw-shared"]);
    }

    [Fact]
    public void WhenSubjectsFormACycle_ThenBuildTerminates()
    {
        // Arrange: raw-root -> raw-child -> raw-root.
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
                {
                    ["Ref"] = new SubjectPropertyUpdate { Kind = SubjectPropertyUpdateKind.Object, Id = "raw-root" }
                }
            }
        };

        // Act
        var idMap = SnapshotIdMap.Build(update);

        // Assert
        Assert.Equal(2, idMap.Count);
        Assert.Equal("ROOT", idMap["raw-root"]);
        Assert.Equal("SUBJ_1", idMap["raw-child"]);
    }
}
