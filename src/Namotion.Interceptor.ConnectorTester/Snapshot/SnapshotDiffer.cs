using System.Text.Json.Nodes;

namespace Namotion.Interceptor.ConnectorTester.Snapshot;

public enum SnapshotDiffKind
{
    PropertyDiffers,
    PropertyMissingFromOther,
    PropertyMissingFromReference,
    SubjectMissingFromOther,
    SubjectMissingFromReference
}

public sealed record SnapshotDiffEntry(
    string SubjectId,
    string? PropertyName,
    SnapshotDiffKind Kind,
    string ReferenceSummary,
    string OtherSummary);

/// <summary>
/// Walks two normalized snapshot JSON strings and produces structured diff entries.
/// CollectFindings preserves today's null-timestamp forgiveness contract:
/// returns [] for fully equal snapshots, populated list for forgiven null-timestamp
/// mismatches, and null for any real divergence.
/// </summary>
public static class SnapshotDiffer
{
    public static IReadOnlyList<SnapshotDiffEntry> Diff(
        string referenceName, string referenceSnapshot,
        string otherName, string otherSnapshot)
    {
        var entries = new List<SnapshotDiffEntry>();

        var reference = SnapshotComparer.ParseSubjects(referenceSnapshot);
        var other = SnapshotComparer.ParseSubjects(otherSnapshot);
        if (reference is null || other is null)
        {
            return entries;
        }

        foreach (var (subjectId, referenceSubjectNode) in reference)
        {
            if (other[subjectId] is not JsonObject otherProperties)
            {
                entries.Add(new SnapshotDiffEntry(
                    subjectId, PropertyName: null,
                    SnapshotDiffKind.SubjectMissingFromOther,
                    ReferenceSummary: $"present in {referenceName}",
                    OtherSummary: $"missing from {otherName}"));
                continue;
            }

            var referenceProperties = referenceSubjectNode!.AsObject();
            foreach (var (propertyName, referencePropertyNode) in referenceProperties)
            {
                if (otherProperties[propertyName] is not JsonObject otherProperty)
                {
                    entries.Add(new SnapshotDiffEntry(
                        subjectId, propertyName,
                        SnapshotDiffKind.PropertyMissingFromOther,
                        ReferenceSummary: $"present in {referenceName}",
                        OtherSummary: $"missing from {otherName}"));
                    continue;
                }

                var referenceProperty = referencePropertyNode!.AsObject();
                if (SnapshotComparer.PropertiesMatch(referenceProperty, otherProperty))
                {
                    continue;
                }

                entries.Add(new SnapshotDiffEntry(
                    subjectId, propertyName,
                    SnapshotDiffKind.PropertyDiffers,
                    SummarizeProperty(referenceProperty),
                    SummarizeProperty(otherProperty)));
            }
        }

        foreach (var (subjectId, otherSubjectNode) in other)
        {
            if (!reference.ContainsKey(subjectId))
            {
                entries.Add(new SnapshotDiffEntry(
                    subjectId, PropertyName: null,
                    SnapshotDiffKind.SubjectMissingFromReference,
                    ReferenceSummary: $"missing from {referenceName}",
                    OtherSummary: $"present in {otherName}"));
                continue;
            }

            if (otherSubjectNode is not JsonObject otherSharedProperties)
            {
                continue;
            }

            var referenceSharedProperties = reference[subjectId]!.AsObject();
            foreach (var (propertyName, _) in otherSharedProperties)
            {
                if (!referenceSharedProperties.ContainsKey(propertyName))
                {
                    entries.Add(new SnapshotDiffEntry(
                        subjectId, propertyName,
                        SnapshotDiffKind.PropertyMissingFromReference,
                        ReferenceSummary: $"missing from {referenceName}",
                        OtherSummary: $"present in {otherName}"));
                }
            }
        }

        return entries;
    }

    public static List<string>? CollectFindings(
        string nameA, string snapshotA,
        string nameB, string snapshotB)
    {
        if (snapshotA == snapshotB)
        {
            return [];
        }

        var subjectsA = SnapshotComparer.ParseSubjects(snapshotA);
        var subjectsB = SnapshotComparer.ParseSubjects(snapshotB);

        if (subjectsA is null || subjectsB is null)
        {
            return (subjectsA is null && subjectsB is null) ? [] : null;
        }

        if (subjectsA.Count != subjectsB.Count)
        {
            return null;
        }

        var findings = new List<string>();

        foreach (var (subjectId, subjectNodeA) in subjectsA)
        {
            if (subjectsB[subjectId] is not JsonObject propertiesB)
            {
                return null;
            }

            var propertiesA = subjectNodeA!.AsObject();
            if (propertiesA.Count != propertiesB.Count)
            {
                return null;
            }

            foreach (var (propertyName, propertyNodeA) in propertiesA)
            {
                if (propertiesB[propertyName] is not JsonObject propertyB)
                {
                    return null;
                }

                var propertyA = propertyNodeA!.AsObject();
                if (!SnapshotComparer.PropertiesMatch(propertyA, propertyB))
                {
                    return null;
                }

                var timestampA = propertyA[SnapshotComparer.TimestampKey];
                var timestampB = propertyB[SnapshotComparer.TimestampKey];
                if ((timestampA is null) != (timestampB is null))
                {
                    var hasTimestamp = timestampA is not null ? nameA : nameB;
                    var missingTimestamp = timestampA is null ? nameA : nameB;
                    var timestampValue = (timestampA ?? timestampB)!.ToJsonString();
                    findings.Add($"{subjectId}.{propertyName}: {hasTimestamp} has timestamp {timestampValue}, {missingTimestamp} has none");
                }
            }
        }

        return findings;
    }

    public static string SummarizeProperty(JsonObject property)
    {
        var kind = property[SnapshotComparer.KindKey]?.GetValue<string>() ?? "?";
        return kind switch
        {
            "Value" => FormatValueSummary(property),
            "Object" => $"Object id={property[SnapshotComparer.IdKey]?.ToJsonString() ?? "null"}",
            "Collection" or "Dictionary" =>
                $"{kind} count={property[SnapshotComparer.CountKey]?.ToJsonString() ?? "?"} " +
                $"items={property[SnapshotComparer.ItemsKey]?.ToJsonString() ?? "[]"}",
            _ => property.ToJsonString()
        };
    }

    private static string FormatValueSummary(JsonObject property)
    {
        var value = property[SnapshotComparer.ValueKey]?.ToJsonString() ?? "null";
        var timestamp = property[SnapshotComparer.TimestampKey]?.GetValue<string>() ?? "never";
        return $"{value} (written {timestamp})";
    }
}
