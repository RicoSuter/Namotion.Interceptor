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
    /// Adds additional properties to this subject (e.g. from an inheriting class or dynamic
    /// context). The call routes through <see cref="Executor"/>: on a subject attached to a
    /// context with a lifecycle, metadata, initial ownership edges and property callbacks publish
    /// as one atomic admission; on an unattached subject only the metadata publishes, and a later
    /// attach discovers the then-current structural properties through their normal getters.
    /// </summary>
    /// <remarks>
    /// The metadata sequence must be synchronous, stable, and free of topology and metadata side
    /// effects. It is materialized exactly once, after callback admission; an iterator that
    /// re-enters the subject or mutates state receives no replay and no rollback. A name that is
    /// already defined on the subject, or appears twice in the batch, rejects the whole batch
    /// before anything is published.
    /// </remarks>
    /// <param name="properties">The additional properties.</param>
    /// <exception cref="InvalidOperationException">A property name is duplicated, part of a
    /// captured structural value belongs to a different context, or the call happened inside a
    /// lifecycle callback of another context.</exception>
    void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties);
    
    // TODO(perf): Use span here?
}
