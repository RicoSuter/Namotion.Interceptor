using System.Runtime.CompilerServices;
using HomeBlaze.Abstractions;
using HomeBlaze.History.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Change;

namespace HomeBlaze.History;

/// <summary>
/// The graph-side glue between a change queue and a history engine: eligibility filtering, canonical
/// path resolution, move detection and throughput counting.
///
/// Shared by every store subject rather than reimplemented per store. The two copies this replaced
/// were identical in intent but had to stay in lockstep by hand, and move detection is the subtle
/// part: it decides which samples a later query can still reach.
/// </summary>
public sealed class HistoryChangeRecorder(
    IHistoryRecorder engine, ISubjectPathResolver resolver, Func<DateTimeOffset>? getUtcNow = null)
{
    /// <summary>What a recorded property was last seen as: its subject's path and its value type.</summary>
    /// <remarks>The type is kept so a property leaving the graph can be closed off without the registry,
    /// which no longer knows the subject by the time the detach is handled.</remarks>
    private readonly record struct RecordedProperty(string SubjectPath, Type Type);

    private readonly Func<DateTimeOffset> _getUtcNow = getUtcNow ?? (() => DateTimeOffset.UtcNow);

    // Last canonical subject path seen per subject and property. Comparing the path the resolver
    // returns against the stored one detects a move without this class subscribing to anything; it
    // inherits whatever freshness the resolver's own cache has (that cache is invalidated from
    // lifecycle changes, so a reordering that attaches and detaches nothing can leave it stale). The
    // per-property inner map lets each history property of a renamed subject detect the move
    // independently, so the first property to change does not consume the rename for its siblings.
    //
    // Weak keys: a detached subject's entry disappears when it becomes unreachable. Lifecycle detach is
    // dispatched through the detaching subject's own context, which never reaches a sibling store, so
    // this cannot rely on being told when a subject goes away.
    private readonly ConditionalWeakTable<IInterceptorSubject, Dictionary<string, RecordedProperty>> _lastSubjectPath = new();
    private readonly Lock _pathCacheLock = new();

    private readonly ThroughputCounter _incomingThroughput = new();
    private readonly ThroughputCounter _recordedThroughput = new();

    /// <summary>Average incoming changes per second (eligible [State] changes observed).</summary>
    public double IncomingChangesPerSecond => _incomingThroughput.CurrentRate;

    /// <summary>Average recorded changes per second (samples the engine accepted).</summary>
    public double RecordedChangesPerSecond => _recordedThroughput.CurrentRate;

    /// <summary>
    /// The change-queue filter: only recordable scalar [State] properties are worth queueing.
    /// </summary>
    public static bool IsEligible(PropertyReference propertyReference) =>
        propertyReference.TryGetRegisteredProperty() is { } registered && registered.HasHistory();

    /// <summary>
    /// Resolves and records one flushed batch of changes.
    /// </summary>
    public ValueTask RecordBatch(ReadOnlyMemory<SubjectPropertyChange> changes)
    {
        var span = changes.Span;
        for (var index = 0; index < span.Length; index++)
        {
            var change = span[index];

            var registered = change.Property.TryGetRegisteredProperty();
            if (registered is null || !registered.HasHistory())
            {
                continue;
            }

            _incomingThroughput.Add(1);

            var subject = change.Property.Subject;
            var subjectPath = resolver.GetPath(subject, PathStyle.Canonical);
            if (subjectPath is null)
            {
                // Subject is no longer reachable (detached between change and flush); skip.
                continue;
            }

            var propertyName = change.Property.Name;
            var fullPath = JoinPath(subjectPath, propertyName);

            lock (_pathCacheLock)
            {
                var pathsByProperty = _lastSubjectPath.GetValue(
                    subject, _ => new Dictionary<string, RecordedProperty>(StringComparer.Ordinal));

                if (pathsByProperty.TryGetValue(propertyName, out var previous) &&
                    !string.Equals(previous.SubjectPath, subjectPath, StringComparison.Ordinal))
                {
                    engine.RecordMove(
                        change.ChangedTimestamp,
                        JoinPath(previous.SubjectPath, propertyName),
                        fullPath);
                }

                pathsByProperty[propertyName] = new RecordedProperty(subjectPath, registered.Type);
            }

            if (engine.TryRecord(fullPath, change.ChangedTimestamp, change.GetNewValue<object>(), registered.Type))
            {
                _recordedThroughput.Add(1);
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Closes off a detached subject's recorded properties and drops its cached paths.
    /// </summary>
    /// <remarks>
    /// Each property is recorded as an explicit null at the instant the subject left the graph, which
    /// the engines already treat as terminating the held value and opening a gap. Without it, Last and
    /// TimeWeightedAverage carry the final reading forward for as long as coverage claims the range, so
    /// a sensor removed in March still charts its last value in December, and after the fact a removed
    /// property is indistinguishable from one whose value simply never changed again.
    ///
    /// Coverage already says when the store was not recording; this says the store was recording and
    /// the property ceased to exist. Only a subject genuinely leaving the graph reaches here: a
    /// collection reorder that keeps its instances raises reference add/remove instead, and process
    /// shutdown stops hosted services without detaching them.
    ///
    /// The weak table would release the cache entry anyway once the subject became unreachable; this
    /// just does not wait for that.
    /// </remarks>
    public void Forget(IInterceptorSubject subject)
    {
        lock (_pathCacheLock)
        {
            if (_lastSubjectPath.TryGetValue(subject, out var pathsByProperty))
            {
                var removedAt = _getUtcNow();
                foreach (var (propertyName, recorded) in pathsByProperty)
                {
                    engine.TryRecord(
                        JoinPath(recorded.SubjectPath, propertyName), removedAt, null, recorded.Type);
                }
            }

            _lastSubjectPath.Remove(subject);
        }
    }

    // Joins a canonical subject path with a property name. The root subject path is "/", so a root
    // property is "/Temperature" (not "//Temperature"); a child at "/Child" yields "/Child/Pressure".
    private static string JoinPath(string subjectPath, string propertyName) =>
        subjectPath == "/" ? "/" + propertyName : subjectPath + "/" + propertyName;
}
