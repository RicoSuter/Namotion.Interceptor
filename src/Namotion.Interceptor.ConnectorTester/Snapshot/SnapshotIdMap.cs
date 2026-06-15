using Namotion.Interceptor.Connectors.Updates;

namespace Namotion.Interceptor.ConnectorTester.Snapshot;

/// <summary>
/// Builds a stable raw-subject-id -> normalized-id map for a SubjectUpdate.
/// The root is renamed to "ROOT"; reachable subjects are numbered SUBJ_1, SUBJ_2, ...
/// in BFS order, with property names sorted ordinally and dictionary items sorted
/// by their key. Used by SnapshotComparer to make snapshots from
/// independently-constructed contexts produce equal byte strings for equal source state.
/// </summary>
public static class SnapshotIdMap
{
    public static Dictionary<string, string> Build(SubjectUpdate update)
    {
        var idMap = new Dictionary<string, string>(StringComparer.Ordinal);
        if (update.Root is null)
        {
            return idMap;
        }

        idMap[update.Root] = "ROOT";
        var queue = new Queue<string>();
        queue.Enqueue(update.Root);

        var counter = 1;

        while (queue.Count > 0)
        {
            var currentRawId = queue.Dequeue();

            if (!update.Subjects.TryGetValue(currentRawId, out var properties))
            {
                continue;
            }

            foreach (var propertyName in properties.Keys.OrderBy(name => name, StringComparer.Ordinal))
            {
                var property = properties[propertyName];

                if (property.Kind == SubjectPropertyUpdateKind.Object)
                {
                    if (property.Id is not null && !idMap.ContainsKey(property.Id))
                    {
                        idMap[property.Id] = $"SUBJ_{counter++}";
                        queue.Enqueue(property.Id);
                    }
                }
                else if (property.Kind is SubjectPropertyUpdateKind.Collection or SubjectPropertyUpdateKind.Dictionary
                    && property.Items is { } items)
                {
                    IEnumerable<SubjectPropertyItemUpdate> orderedItems =
                        property.Kind == SubjectPropertyUpdateKind.Dictionary
                            ? items.OrderBy(item => item.Key, StringComparer.Ordinal)
                            : items;

                    foreach (var item in orderedItems)
                    {
                        if (!idMap.ContainsKey(item.Id))
                        {
                            idMap[item.Id] = $"SUBJ_{counter++}";
                            queue.Enqueue(item.Id);
                        }
                    }
                }
            }
        }

        return idMap;
    }
}
