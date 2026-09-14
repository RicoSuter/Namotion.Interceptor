using System.Collections.Concurrent;

namespace Namotion.Interceptor;

public interface IInterceptorSubject
{
    /// <summary>
    /// Gets the sync root used to synchronize read/writes of property fields.
    /// </summary>
    object SyncRoot { get; }
    
    /// <summary>
    /// Gets the interceptor collection.
    /// </summary>
    IInterceptorSubjectContext Context { get; }

    /// <summary>
    /// Gets the additional data of this subject.
    /// </summary>
    ConcurrentDictionary<(string? property, string key), object?> Data { get; }

    /// <summary>
    /// Gets the reflected properties (should be cached).
    /// </summary>
    IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties { get; }
    
    /// <summary>
    /// Adds additional properties to this subject (e.g. from an inheriting class or dynamic context).
    /// </summary>
    /// <param name="properties">The additional properties.</param>
    void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties);
    
    // TODO(perf): Use span here?
}
