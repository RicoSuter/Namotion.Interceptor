namespace Namotion.Interceptor.Sources.Updates;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

public static class SubjectUpdatePathExtensions
{
    // Simple pool for rename buffers to avoid repeated allocations
    private static readonly ThreadLocal<Stack<List<(string oldKey, string newKey)>>> RenameBufferPool =
        new(() => new Stack<List<(string, string)>>());

    // Monotonic traversal id for in-place conversions to avoid HashSet visited sets
    private static int _traversalId;

    private static List<(string oldKey, string newKey)> RentRenameBuffer()
    {
        var pool = RenameBufferPool.Value!;
        return pool.Count > 0 ? pool.Pop() : new List<(string, string)>();
    }

    private static void ReturnRenameBuffer(List<(string oldKey, string newKey)> buffer)
    {
        buffer.Clear();
        RenameBufferPool.Value!.Push(buffer);
    }

    public static SubjectUpdate ConvertPathSegments(this SubjectUpdate update,
        Func<string, string?> convertPropertyName,
        Func<string, string?> convertAttributeName)
    {
        return update with
        {
            Properties = update.Properties.ToDictionary(
                p => convertPropertyName(p.Key) ?? p.Key,
                p => p.Value.ConvertPathSegments(convertPropertyName, convertAttributeName))
        };
    }

    public static SubjectPropertyUpdate ConvertPathSegments(this SubjectPropertyUpdate update,
        Func<string, string?> convertPropertyName,
        Func<string, string?> convertAttributeName)
    {
        return update with
        {
            Item = update.Item?.ConvertPathSegments(convertPropertyName, convertAttributeName),
            Attributes = update.Attributes?.ToDictionary(
                a => convertAttributeName(a.Key) ?? a.Key,
                a => a.Value.ConvertPathSegments(convertPropertyName, convertAttributeName)),
            Collection = update.Collection?
                .Select(i => new SubjectPropertyCollectionUpdate
                {
                    Index = i.Index,
                    Item = i.Item?.ConvertPathSegments(convertPropertyName, convertAttributeName)
                })
                .ToList()
        };
    }

    public static SubjectUpdate ConvertPathSegmentsInPlace(this SubjectUpdate update,
        Func<string, string?> convertPropertyName,
        Func<string, string?> convertAttributeName)
    {
        var traversalId = Interlocked.Increment(ref _traversalId);
        return ConvertPathSegmentsInPlaceCore(
            update,
            convertPropertyName,
            convertAttributeName,
            traversalId);
    }

    private static SubjectUpdate ConvertPathSegmentsInPlaceCore(this SubjectUpdate update,
        Func<string, string?> convertPropertyName,
        Func<string, string?> convertAttributeName,
        int traversalId)
    {
        if (update._visitMarker == traversalId)
            return update;
        update._visitMarker = traversalId;

        if (update.Properties.Count > 0)
        {
            List<(string oldKey, string newKey)>? renames = null;
            foreach (var kvp in update.Properties)
            {
                var newKey = convertPropertyName(kvp.Key);
                if (newKey is not null && newKey != kvp.Key)
                {
                    renames ??= RentRenameBuffer();
                    renames.Add((kvp.Key, newKey));
                }
                kvp.Value.ConvertPathSegmentsInPlaceCore(convertPropertyName, convertAttributeName, traversalId);
            }
            if (renames is not null)
            {
                foreach (var (oldKey, newKey) in renames)
                {
                    if (update.Properties.TryGetValue(oldKey, out var value))
                    {
                        if (!update.Properties.TryGetValue(newKey, out var existing) || !ReferenceEquals(existing, value))
                        {
                            update.Properties.Remove(oldKey);
                            update.Properties[newKey] = value;
                        }
                    }
                }
                ReturnRenameBuffer(renames);
            }
        }
        return update;
    }

    public static SubjectPropertyUpdate ConvertPathSegmentsInPlace(this SubjectPropertyUpdate update,
        Func<string, string?> convertPropertyName,
        Func<string, string?> convertAttributeName)
    {
        var traversalId = Interlocked.Increment(ref _traversalId);
        return ConvertPathSegmentsInPlaceCore(
            update,
            convertPropertyName,
            convertAttributeName,
            traversalId);
    }

    private static SubjectPropertyUpdate ConvertPathSegmentsInPlaceCore(this SubjectPropertyUpdate update,
        Func<string, string?> convertPropertyName,
        Func<string, string?> convertAttributeName,
        int traversalId)
    {
        if (update._visitMarker == traversalId)
            return update;
        update._visitMarker = traversalId;

        update.Item?.ConvertPathSegmentsInPlaceCore(convertPropertyName, convertAttributeName, traversalId);
        var attributes = update.Attributes;
        if (attributes is not null && attributes.Count > 0)
        {
            List<(string oldKey, string newKey)>? renames = null;
            foreach (var kvp in attributes)
            {
                var newKey = convertAttributeName(kvp.Key);
                if (newKey is not null && newKey != kvp.Key)
                {
                    renames ??= RentRenameBuffer();
                    renames.Add((kvp.Key, newKey));
                }
                kvp.Value.ConvertPathSegmentsInPlaceCore(convertPropertyName, convertAttributeName, traversalId);
            }
            if (renames is not null)
            {
                foreach (var (oldKey, newKey) in renames)
                {
                    if (attributes.TryGetValue(oldKey, out var value))
                    {
                        if (!attributes.TryGetValue(newKey, out var existing) || !ReferenceEquals(existing, value))
                        {
                            attributes.Remove(oldKey);
                            attributes[newKey] = value;
                        }
                    }
                }
                ReturnRenameBuffer(renames);
            }
        }
        var collection = update.Collection;
        if (collection is not null && collection.Count > 0)
        {
            foreach (var item in collection)
            {
                item.Item?.ConvertPathSegmentsInPlaceCore(convertPropertyName, convertAttributeName, traversalId);
            }
        }
        return update;
    }
}