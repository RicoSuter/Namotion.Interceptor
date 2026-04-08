using System;
using System.Collections;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.WebSocket.Internal;

/// <summary>
/// Computes a deterministic hash of the graph's structural state for divergence detection.
/// Only hashes structure (subject IDs, collection/dictionary membership, object references)
/// — not value properties. Structural divergence is the critical issue that doesn't self-heal.
/// Value divergence self-heals via continued mutations or is fixed as a side effect when
/// the structural hash triggers a re-sync (Welcome includes complete state with values).
/// </summary>
internal static class StateHashComputer
{
    /// <summary>
    /// Computes a SHA256 hash of the graph's structural state.
    /// Walks the registered subject graph and hashes subject IDs + structural property content
    /// (collection items, dictionary entries, object references) in sorted order.
    /// </summary>
    public static string? ComputeStructuralHash(IInterceptorSubject rootSubject)
    {
        var registry = rootSubject.Context.TryGetService<ISubjectRegistry>();
        if (registry is null)
            return null;

        var knownSubjects = registry.KnownSubjects;
        if (knownSubjects.Count == 0)
            return null;

        // Build a deterministic string representation of the structural state.
        // Sort by subject ID for deterministic ordering.
        var sortedIds = new SortedDictionary<string, RegisteredSubject>(StringComparer.Ordinal);
        foreach (var (subject, registered) in knownSubjects)
        {
            var id = subject.TryGetSubjectId();
            if (id is not null)
            {
                sortedIds[id] = registered;
            }
        }

        var builder = new StringBuilder(sortedIds.Count * 64);
        foreach (var (subjectId, registered) in sortedIds)
        {
            builder.Append(subjectId);

            foreach (var property in registered.Properties)
            {
                if (!property.CanContainSubjects || !property.HasGetter)
                    continue;

                var value = property.GetValue();
                builder.Append('|').Append(property.Name).Append(':');

                switch (value)
                {
                    case IInterceptorSubject refSubject:
                        var refId = refSubject.TryGetSubjectId();
                        builder.Append(refId ?? "?");
                        break;

                    case IDictionary dictionary:
                        builder.Append('{');
                        foreach (DictionaryEntry entry in dictionary)
                        {
                            if (entry.Value is IInterceptorSubject dictSubject)
                            {
                                builder.Append(entry.Key).Append('=');
                                builder.Append(dictSubject.TryGetSubjectId() ?? "?").Append(',');
                            }
                        }
                        builder.Append('}');
                        break;

                    case ICollection collection:
                        builder.Append('[');
                        foreach (var item in collection)
                        {
                            if (item is IInterceptorSubject colSubject)
                            {
                                builder.Append(colSubject.TryGetSubjectId() ?? "?").Append(',');
                            }
                        }
                        builder.Append(']');
                        break;

                    default:
                        builder.Append('-');
                        break;
                }
            }

            builder.Append('\n');
        }

        var bytes = Encoding.UTF8.GetBytes(builder.ToString());
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }
}
