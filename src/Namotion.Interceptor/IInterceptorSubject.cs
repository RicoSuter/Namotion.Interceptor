using System.Collections.Concurrent;
using Namotion.Interceptor.Interceptors;

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
    /// Gets the executor that runs this subject's interception and owns its exact context
    /// attachment. During the single-context transition this is the same object
    /// <see cref="Context"/> returns; the transition ends with <see cref="Context"/> removed and
    /// this member as the only access path.
    /// </summary>
    IInterceptorExecutor Executor { get; }

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
