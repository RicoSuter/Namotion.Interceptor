using System.Collections.Frozen;

namespace Namotion.Interceptor;

public static class InterceptorSubjectExtensions
{
    public static void SetData(this IInterceptorSubject subject, string key, object? value)
    {
        subject.Data[(null, key)] = value;
    }

    public static bool TryGetData(this IInterceptorSubject subject, string key, out object? value)
    {
        return subject.Data.TryGetValue((null, key), out value);
    }

    /// <summary>
    /// Adds subject data for the specified key only if the key is not already present.
    /// This operation is atomic and thread-safe, so it doubles as a one-shot latch: it returns
    /// <c>true</c> exactly once per subject and key.
    /// </summary>
    /// <returns><c>true</c> if the value was stored; <c>false</c> if a value was already present.</returns>
    public static bool TryAddData(this IInterceptorSubject subject, string key, object? value)
    {
        return subject.Data.TryAdd((null, key), value);
    }

    /// <summary>
    /// Merges additional properties into the subject's current property set, for an
    /// <see cref="IInterceptorSubject.AddProperties"/> implementation to store. The merge starts from
    /// <see cref="IInterceptorSubject.Properties"/>, so the property set of a subclass is kept.
    /// </summary>
    /// <param name="subject">The subject.</param>
    /// <param name="properties">The properties to add.</param>
    /// <returns>The merged property set.</returns>
    /// <exception cref="InvalidOperationException">A property name is already present on the subject or
    /// appears twice in <paramref name="properties"/>. Nothing is merged in that case.</exception>
    /// <remarks>The caller holds <see cref="IInterceptorSubject.SyncRoot"/>; this method does not lock.</remarks>
    public static FrozenDictionary<string, SubjectPropertyMetadata> MergeProperties(
        this IInterceptorSubject subject, IEnumerable<SubjectPropertyMetadata> properties)
    {
        var existing = subject.Properties;
        var merged = new Dictionary<string, SubjectPropertyMetadata>(
            properties.TryGetNonEnumeratedCount(out var count) ? existing.Count + count : existing.Count);
        foreach (var entry in existing)
        {
            merged.Add(entry.Key, entry.Value);
        }

        foreach (var property in properties)
        {
            if (!merged.TryAdd(property.Name, property))
            {
                throw new InvalidOperationException(existing.ContainsKey(property.Name)
                    ? $"The subject '{subject.GetType().Name}' already has a property named '{property.Name}'."
                    : $"The property '{property.Name}' appears more than once in the properties added to subject '{subject.GetType().Name}'.");
            }
        }

        return merged.ToFrozenDictionary();
    }
}
