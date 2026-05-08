using System.Collections.Concurrent;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// A thread-safe reverse-index map from external IDs to interceptor subjects with ref counting
/// and automatic lifecycle-based cleanup.
/// </summary>
/// <remarks>
/// When a subject is detached from the object graph (via <see cref="LifecycleInterceptor.SubjectDetaching"/>),
/// entries pointing to that subject have their ref count decremented. Entries are removed when the
/// ref count reaches zero.
/// </remarks>
/// <typeparam name="TExternalId">The type of external identifier (e.g., OPC UA NodeId, MQTT topic).</typeparam>
public class ConnectorSubjectMap<TExternalId> : IDisposable
    where TExternalId : notnull
{
    private readonly ConcurrentDictionary<TExternalId, (IInterceptorSubject Subject, int RefCount)> _entries = new();
    private readonly Lock _lock = new();
    private readonly LifecycleInterceptor? _lifecycleInterceptor;

    private int _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectorSubjectMap{TExternalId}"/> class.
    /// </summary>
    /// <param name="context">The interceptor subject context to hook lifecycle events from.</param>
    public ConnectorSubjectMap(IInterceptorSubjectContext context)
    {
        _lifecycleInterceptor = context.TryGetLifecycleInterceptor();
        if (_lifecycleInterceptor is not null)
        {
            _lifecycleInterceptor.SubjectDetaching += OnSubjectDetaching;
        }
    }

    /// <summary>
    /// Gets all tracked external IDs.
    /// </summary>
    public IEnumerable<TExternalId> ExternalIds => _entries.Keys;

    /// <summary>
    /// Registers a mapping from an external ID to a subject.
    /// Increments the ref count if the external ID is already tracked.
    /// </summary>
    /// <param name="externalId">The external identifier.</param>
    /// <param name="subject">The subject to map to.</param>
    public void Add(TExternalId externalId, IInterceptorSubject subject)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(externalId, out var existing))
            {
                _entries[externalId] = (existing.Subject, existing.RefCount + 1);
            }
            else
            {
                _entries[externalId] = (subject, 1);
            }
        }
    }

    /// <summary>
    /// Looks up a subject by its external ID.
    /// </summary>
    /// <param name="externalId">The external identifier to look up.</param>
    /// <param name="subject">The subject if found; otherwise <c>null</c>.</param>
    /// <returns><c>true</c> if the external ID was found; otherwise <c>false</c>.</returns>
    public bool TryGetSubject(TExternalId externalId, out IInterceptorSubject? subject)
    {
        if (_entries.TryGetValue(externalId, out var entry))
        {
            subject = entry.Subject;
            return true;
        }

        subject = null;
        return false;
    }

    /// <summary>
    /// Decrements the ref count for an external ID. Removes the entry when the ref count reaches zero.
    /// </summary>
    /// <param name="externalId">The external identifier to remove.</param>
    /// <returns><c>true</c> when the ref count hit zero and the entry was removed; otherwise <c>false</c>.</returns>
    public bool Remove(TExternalId externalId)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(externalId, out var entry))
            {
                return false;
            }

            if (entry.RefCount > 1)
            {
                _entries[externalId] = (entry.Subject, entry.RefCount - 1);
                return false;
            }

            _entries.TryRemove(externalId, out _);
            return true;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        if (_lifecycleInterceptor is not null)
        {
            _lifecycleInterceptor.SubjectDetaching -= OnSubjectDetaching;
        }

        _entries.Clear();
    }

    private void OnSubjectDetaching(SubjectLifecycleChange change)
    {
        lock (_lock)
        {
            // Collect keys to modify/remove to avoid modifying while iterating.
            List<TExternalId>? keysToRemove = null;
            List<(TExternalId Key, int NewRefCount)>? keysToDecrement = null;

            foreach (var kvp in _entries)
            {
                if (ReferenceEquals(kvp.Value.Subject, change.Subject))
                {
                    var newRefCount = kvp.Value.RefCount - 1;
                    if (newRefCount <= 0)
                    {
                        keysToRemove ??= [];
                        keysToRemove.Add(kvp.Key);
                    }
                    else
                    {
                        keysToDecrement ??= [];
                        keysToDecrement.Add((kvp.Key, newRefCount));
                    }
                }
            }

            if (keysToRemove is not null)
            {
                foreach (var key in keysToRemove)
                {
                    _entries.TryRemove(key, out _);
                }
            }

            if (keysToDecrement is not null)
            {
                foreach (var (key, newRefCount) in keysToDecrement)
                {
                    if (_entries.TryGetValue(key, out var entry))
                    {
                        _entries[key] = (entry.Subject, newRefCount);
                    }
                }
            }
        }
    }
}
