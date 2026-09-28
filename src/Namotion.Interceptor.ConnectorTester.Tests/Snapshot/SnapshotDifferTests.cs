using Xunit;
using Namotion.Interceptor.ConnectorTester.Snapshot;

namespace Namotion.Interceptor.ConnectorTester.Tests.Snapshot;

public class SnapshotDifferTests
{
    [Fact]
    public void WhenSnapshotsEqual_ThenDiffIsEmpty()
    {
        // Arrange
        const string snapshot = """{"root":"ROOT","subjects":{"ROOT":{"P":{"kind":"Value","value":1}}}}""";

        // Act
        var entries = SnapshotDiffer.Diff("server", snapshot, "client", snapshot);

        // Assert
        Assert.Empty(entries);
    }

    [Fact]
    public void WhenValueDiffers_ThenDifferenceEntryIsReturned()
    {
        // Arrange
        const string reference = """{"root":"ROOT","subjects":{"ROOT":{"P":{"kind":"Value","value":1,"timestamp":"2026-01-01T00:00:00+00:00"}}}}""";
        const string other     = """{"root":"ROOT","subjects":{"ROOT":{"P":{"kind":"Value","value":2,"timestamp":"2026-01-02T00:00:00+00:00"}}}}""";

        // Act
        var entries = SnapshotDiffer.Diff("server", reference, "client", other);

        // Assert
        var entry = Assert.Single(entries);
        Assert.Equal("ROOT", entry.SubjectId);
        Assert.Equal("P", entry.PropertyName);
        Assert.Contains("1", entry.ReferenceSummary);
        Assert.Contains("2", entry.OtherSummary);
    }

    [Fact]
    public void WhenSubjectMissingFromOther_ThenMissingFromOtherEntryIsReturned()
    {
        // Arrange
        const string reference = """{"root":"ROOT","subjects":{"ROOT":{"R":{"kind":"Object","id":"OTHER"}},"OTHER":{"P":{"kind":"Value","value":1}}}}""";
        const string other     = """{"root":"ROOT","subjects":{"ROOT":{"R":{"kind":"Object","id":"OTHER"}}}}""";

        // Act
        var entries = SnapshotDiffer.Diff("server", reference, "client", other);

        // Assert: at least one entry reports the missing subject.
        Assert.Contains(entries, e => e.SubjectId == "OTHER" && e.Kind == SnapshotDiffKind.SubjectMissingFromOther);
    }

    [Fact]
    public void WhenSubjectMissingFromReference_ThenMissingFromReferenceEntryIsReturned()
    {
        // Arrange
        const string reference = """{"root":"ROOT","subjects":{"ROOT":{"R":{"kind":"Object","id":"OTHER"}}}}""";
        const string other     = """{"root":"ROOT","subjects":{"ROOT":{"R":{"kind":"Object","id":"OTHER"}},"OTHER":{"P":{"kind":"Value","value":1}}}}""";

        // Act
        var entries = SnapshotDiffer.Diff("server", reference, "client", other);

        // Assert
        Assert.Contains(entries, e => e.SubjectId == "OTHER" && e.Kind == SnapshotDiffKind.SubjectMissingFromReference);
    }

    [Fact]
    public void WhenCollectFindingsCalledOnNullTimestampMismatch_ThenReturnsForgivenFinding()
    {
        // Arrange
        const string a = """{"root":"ROOT","subjects":{"ROOT":{"P":{"kind":"Value","value":1,"timestamp":"2026-01-01T00:00:00+00:00"}}}}""";
        const string b = """{"root":"ROOT","subjects":{"ROOT":{"P":{"kind":"Value","value":1}}}}""";

        // Act
        var findings = SnapshotDiffer.CollectFindings("server", a, "client", b);

        // Assert
        Assert.NotNull(findings);
        Assert.Single(findings);
        Assert.Contains("server has timestamp", findings[0]);
        Assert.Contains("client has none", findings[0]);
    }

    [Fact]
    public void WhenCollectFindingsCalledOnRealMismatch_ThenReturnsNull()
    {
        // Arrange
        const string a = """{"root":"ROOT","subjects":{"ROOT":{"P":{"kind":"Value","value":1}}}}""";
        const string b = """{"root":"ROOT","subjects":{"ROOT":{"P":{"kind":"Value","value":2}}}}""";

        // Act
        var findings = SnapshotDiffer.CollectFindings("server", a, "client", b);

        // Assert
        Assert.Null(findings);
    }
}
