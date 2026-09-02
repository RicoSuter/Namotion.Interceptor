using System.Collections.Immutable;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Tracking.Lifecycle;

internal sealed class LifecycleNotifier(
    IInterceptorSubjectContext context,
    LifecycleInterceptor originatingLifecycle)
{
    internal sealed record LifecycleJournal(ImmutableArray<Action> Entries)
    {
        internal Exception? Drain(Exception? primaryException)
        {
            List<Exception>? failures = primaryException is null ? null : [primaryException];
            foreach (var entry in Entries)
            {
                try
                {
                    entry();
                }
                catch (Exception exception)
                {
                    (failures ??= []).Add(exception);
                }
            }

            return failures switch
            {
                null => null,
                { Count: 1 } => failures[0],
                _ => new AggregateException(failures)
            };
        }
    }

    [ThreadStatic]
    private static JournalBuilder? _currentJournal;

    private static long _projectionRevision;
    private static long _reservedProjectionRevisionCapacity;
    private static readonly Lock ProjectionRevisionLock = new();

    // One-shot deterministic failure injection for the journal/publication atomicity tests.
    private Exception? _journalCompletionFailure;

    internal void FailNextJournalCompletionForTests(Exception failure)
    {
        if (Interlocked.CompareExchange(ref _journalCompletionFailure, failure, null) is not null)
        {
            throw new InvalidOperationException("A journal completion failure is already armed.");
        }
    }

    public event Action<SubjectLifecycleChange>? SubjectAttached;
    public event Action<SubjectLifecycleChange>? SubjectDetaching;

    internal JournalCapture BeginJournal()
    {
        if (_currentJournal is not null)
        {
            throw new InvalidOperationException("A lifecycle journal is already being prepared on this thread.");
        }

        _currentJournal = new JournalBuilder(
            this,
            context.GetServices<ILifecycleHandler>(),
            context.GetServices<IPropertyLifecycleHandler>(),
            GetEventHandlers(SubjectAttached),
            GetEventHandlers(SubjectDetaching));
        return new JournalCapture(_currentJournal);
    }

    internal static void ThrowIfTopologyChange(InterceptorSubjectContext context)
    {
        if (InterceptorExecutor.IsInsideLogicalCallback ||
            !InterceptorExecutor.IsCurrentLogicalContext(context))
        {
            throw new LifecycleContractViolationException(
                "A lifecycle callback must not change graph topology, and a thread runs topology " +
                "work for at most one subject context at a time. Defer the operation until the " +
                "current callback or operation completes.");
        }
    }

    internal static void ThrowIfOtherContext(InterceptorSubjectContext context)
    {
        if (!InterceptorExecutor.IsCurrentLogicalContext(context))
        {
            throw new LifecycleContractViolationException(
                "A thread runs topology work for at most one subject context at a time. Defer the " +
                "second-context operation until the current operation completes.");
        }
    }

    internal SubjectLifecycleChange CompleteChange(
        SubjectLifecycleChange change,
        ImmutableArray<SubjectPropertyMetadata> properties,
        ImmutableArray<SubjectParent> parents,
        StructuralSnapshot propertySnapshot)
    {
        return new SubjectLifecycleChange
        {
            Context = context,
            Revision = NextProjectionRevision(),
            Subject = change.Subject,
            Properties = properties,
            Parents = parents,
            Property = change.Property,
            PropertyChildren = ToChildren(propertySnapshot),
            Index = change.Index,
            ReferenceCount = change.ReferenceCount,
            IsContextAttach = change.IsContextAttach,
            IsPropertyReferenceAdded = change.IsPropertyReferenceAdded,
            IsPropertyReferenceRemoved = change.IsPropertyReferenceRemoved,
            IsContextDetach = change.IsContextDetach
        };
    }

    public void RaiseSubjectAttached(SubjectLifecycleChange change) =>
        RecordEvent(true, change);

    public void RaiseSubjectDetaching(SubjectLifecycleChange change) =>
        RecordEvent(false, change);

    internal void InvokePreparedAddedLifecycleHandlers(
        ILifecycleHandler? subjectHandler, SubjectLifecycleChange change, Action? prepareChildren)
    {
        foreach (var handler in GetLifecycleHandlers())
        {
            if (ReferenceEquals(handler, originatingLifecycle))
            {
                prepareChildren?.Invoke();
            }
            else
            {
                Record(() => handler.HandleLifecycleChange(change));
            }
        }

        if (subjectHandler is not null)
        {
            Record(() => subjectHandler.HandleLifecycleChange(change));
        }
    }

    public void InvokeRemovedLifecycleHandlers(
        ILifecycleHandler? subjectHandler, SubjectLifecycleChange change)
    {
        if (subjectHandler is not null)
        {
            Record(() => subjectHandler.HandleLifecycleChange(change));
        }

        foreach (var handler in GetLifecycleHandlers())
        {
            Record(() => handler.HandleLifecycleChange(change));
        }
    }

    public void AttachSubjectProperties(
        IInterceptorSubject subject, IPropertyLifecycleHandler? subjectHandler,
        IEnumerable<SubjectPropertyMetadata> properties,
        IReadOnlyDictionary<PropertyReference, StructuralSnapshot> snapshots,
        OwnershipGraph.GraphState state) =>
        RecordProperties(subject, subjectHandler, properties, snapshots, state, attach: true);

    public void DetachSubjectProperties(
        IInterceptorSubject subject, IPropertyLifecycleHandler? subjectHandler,
        IEnumerable<SubjectPropertyMetadata> properties) =>
        RecordProperties(subject, subjectHandler, properties, null, null, attach: false);

    private void RecordProperties(
        IInterceptorSubject subject,
        IPropertyLifecycleHandler? subjectHandler,
        IEnumerable<SubjectPropertyMetadata> properties,
        IReadOnlyDictionary<PropertyReference, StructuralSnapshot>? snapshots,
        OwnershipGraph.GraphState? state,
        bool attach)
    {
        foreach (var metadata in properties)
        {
            var property = new PropertyReference(subject, metadata.Name);
            var change = new SubjectPropertyLifecycleChange(subject, property)
            {
                Context = context,
                Revision = NextProjectionRevision(),
                Metadata = metadata,
                Children = snapshots is not null && snapshots.TryGetValue(property, out var snapshot)
                    ? ToChildren(snapshot)
                    : [],
                ChildSubjects = snapshots is not null && state is not null && snapshots.TryGetValue(property, out snapshot)
                    ? ToSubjectProjections(snapshot, state)
                    : []
            };
            foreach (var handler in GetPropertyHandlers())
            {
                Record(() => InvokeProperty(handler, change, attach));
            }

            if (subjectHandler is not null)
            {
                Record(() => InvokeProperty(subjectHandler, change, attach));
            }
        }
    }

    private static void InvokeProperty(
        IPropertyLifecycleHandler handler,
        SubjectPropertyLifecycleChange change,
        bool attach) =>
        (attach ? (Action<SubjectPropertyLifecycleChange>)handler.AttachProperty : handler.DetachProperty)(change);

    public void RefreshCollectionProperty(
        PropertyReference property, StructuralSnapshot snapshot, OwnershipGraph.GraphState state)
    {
        var ownership = state.Owned[property.Subject];
        var change = new SubjectPropertyLifecycleChange(property.Subject, property)
        {
            Context = context,
            Revision = NextProjectionRevision(),
            Metadata = ownership.Properties.First(metadata => metadata.Name == property.Name),
            Children = ToChildren(snapshot),
            ChildSubjects = ToSubjectProjections(snapshot, state)
        };
        foreach (var handler in GetPropertyHandlers())
        {
            Record(() => handler.RefreshCollectionProperty(change));
        }

        if (property.Subject is IPropertyLifecycleHandler subjectHandler)
        {
            Record(() => subjectHandler.RefreshCollectionProperty(change));
        }
    }

    private static ImmutableArray<(IInterceptorSubject Subject, object? Index)> ToChildren(
        StructuralSnapshot snapshot)
    {
        if (snapshot.Occurrences.IsEmpty)
        {
            return [];
        }

        var children = ImmutableArray.CreateBuilder<(IInterceptorSubject, object?)>(snapshot.Occurrences.Length);
        foreach (var occurrence in snapshot.Occurrences)
        {
            children.Add((occurrence.Subject, occurrence.Index));
        }

        return children.MoveToImmutable();
    }

    private static long NextProjectionRevision()
    {
        var builder = _currentJournal ?? throw new InvalidOperationException(
            "Lifecycle projections must be prepared inside a journal.");
        if (builder.TryTakeReservedRevision(out var reserved))
        {
            return reserved;
        }

        return AllocateProjectionRevisions(1);
    }

    private static long AllocateProjectionRevisions(long count)
    {
        lock (ProjectionRevisionLock)
        {
            ValidateProjectionRevisionCapacity(count);
            var first = _projectionRevision + 1;
            _projectionRevision += count;
            return first;
        }
    }

    private static void ReserveProjectionRevisionCapacity(long count)
    {
        lock (ProjectionRevisionLock)
        {
            ValidateProjectionRevisionCapacity(count);
            _reservedProjectionRevisionCapacity += count;
        }
    }

    private static long AllocateReservedProjectionRevisions(long count)
    {
        lock (ProjectionRevisionLock)
        {
            if (count < 0 || count > _reservedProjectionRevisionCapacity)
            {
                throw new InvalidOperationException("The lifecycle journal exceeded its reserved revision capacity.");
            }

            _reservedProjectionRevisionCapacity -= count;
            var first = _projectionRevision + 1;
            _projectionRevision += count;
            return first;
        }
    }

    private static void ReleaseProjectionRevisionCapacity(long count)
    {
        lock (ProjectionRevisionLock)
        {
            _reservedProjectionRevisionCapacity -= count;
        }
    }

    private static void ValidateProjectionRevisionCapacity(long count)
    {
        var available = long.MaxValue - _projectionRevision;
        if (_projectionRevision < 0 || count < 0 ||
            _reservedProjectionRevisionCapacity < 0 ||
            _reservedProjectionRevisionCapacity > available ||
            count > available - _reservedProjectionRevisionCapacity)
        {
            throw new InvalidOperationException(
                "The lifecycle projection revision space is exhausted; publication cannot continue safely.");
        }
    }

    private static ImmutableArray<(IInterceptorSubject Subject, ImmutableArray<SubjectParent> Parents)> ToSubjectProjections(
        StructuralSnapshot snapshot, OwnershipGraph.GraphState state)
    {
        if (snapshot.Occurrences.IsEmpty)
        {
            return [];
        }

        var seen = new HashSet<IInterceptorSubject>(ReferenceEqualityComparer.Instance);
        var projections = ImmutableArray.CreateBuilder<(
            IInterceptorSubject Subject, ImmutableArray<SubjectParent> Parents)>();
        foreach (var occurrence in snapshot.Occurrences)
        {
            if (seen.Add(occurrence.Subject) &&
                state.Owned.TryGetValue(occurrence.Subject, out var ownership))
            {
                projections.Add((occurrence.Subject, ownership.Parents));
            }
        }

        return projections.ToImmutable();
    }

    private ImmutableArray<ILifecycleHandler> GetLifecycleHandlers() => CurrentBuilder.LifecycleHandlers;

    private ImmutableArray<IPropertyLifecycleHandler> GetPropertyHandlers() => CurrentBuilder.PropertyHandlers;

    private JournalBuilder CurrentBuilder =>
        _currentJournal is { Owner: var owner } builder && ReferenceEquals(owner, this)
            ? builder
            : throw new InvalidOperationException("Lifecycle callbacks must be prepared inside the originating journal.");

    internal int JournalEntryCount => CurrentBuilder.Entries.Count;

    internal List<Action>? DeferJournalEntriesFrom(int start)
    {
        var entries = CurrentBuilder.Entries;
        if (entries.Count == start)
        {
            return null;
        }

        var deferred = entries.GetRange(start, entries.Count - start);
        entries.RemoveRange(start, entries.Count - start);
        return deferred;
    }

    internal void AppendJournalEntries(List<Action>? entries)
    {
        if (entries is not null)
        {
            CurrentBuilder.Entries.AddRange(entries);
        }
    }

    internal void FinalizeAttachmentTransitionsAfterJournal(
        ImmutableArray<OwnershipGraph.AttachmentPlan> attachments,
        ImmutableArray<OwnershipGraph.DetachmentPlan> detachments)
    {
        if (!attachments.IsEmpty || !detachments.IsEmpty)
        {
            CurrentBuilder.Entries.Add(
                () =>
                {
                    originatingLifecycle.CompleteAttachments(attachments);
                    originatingLifecycle.CompleteDetachments(detachments);
                    originatingLifecycle.CompleteDeferredSweep();
                });
        }
    }

    private void RecordEvent(bool attach, SubjectLifecycleChange change)
    {
        var builder = CurrentBuilder;
        var handlers = attach ? builder.SubjectAttachedHandlers : builder.SubjectDetachingHandlers;
        foreach (var handler in handlers)
        {
            Record(() => handler(change));
        }
    }

    private static ImmutableArray<Action<SubjectLifecycleChange>> GetEventHandlers(
        Action<SubjectLifecycleChange>? handlers) =>
        handlers is null
            ? []
            : [.. handlers.GetInvocationList().Cast<Action<SubjectLifecycleChange>>()];

    private void Record(Action action)
    {
        void Invoke()
        {
            using var scope = InterceptorExecutor.EnterLogicalCallback((InterceptorSubjectContext)context);
            action();
        }

        CurrentBuilder.Entries.Add(Invoke);
    }

    internal sealed record JournalBuilder(
        LifecycleNotifier Owner,
        ImmutableArray<ILifecycleHandler> LifecycleHandlers,
        ImmutableArray<IPropertyLifecycleHandler> PropertyHandlers,
        ImmutableArray<Action<SubjectLifecycleChange>> SubjectAttachedHandlers,
        ImmutableArray<Action<SubjectLifecycleChange>> SubjectDetachingHandlers)
    {
        private long _nextReservedRevision = -1;
        private long _lastReservedRevision = -1;
        private long _reservedRevisionCapacity;

        internal List<Action> Entries { get; } = [];

        internal void ReserveRevisions(long count)
        {
            if (count == 0) return;
            ReserveProjectionRevisionCapacity(count);
            _reservedRevisionCapacity = count;
        }

        internal bool TryTakeReservedRevision(out long revision)
        {
            if (_nextReservedRevision < 0 && _reservedRevisionCapacity > 0)
            {
                _nextReservedRevision = AllocateReservedProjectionRevisions(_reservedRevisionCapacity);
                _lastReservedRevision = _nextReservedRevision + _reservedRevisionCapacity - 1;
                _reservedRevisionCapacity = 0;
            }

            revision = _nextReservedRevision;
            if (revision < 0 || revision > _lastReservedRevision) return false;
            _nextReservedRevision = revision + 1;
            return true;
        }

        internal void ReleaseReservedRevisionCapacity()
        {
            if (_reservedRevisionCapacity > 0)
            {
                ReleaseProjectionRevisionCapacity(_reservedRevisionCapacity);
                _reservedRevisionCapacity = 0;
            }
        }
    }

    internal readonly struct JournalCapture(JournalBuilder builder) : IDisposable
    {
        internal void PreflightCompletion(long projectionRevisionCapacity = 0)
        {
            if (Interlocked.Exchange(ref builder.Owner._journalCompletionFailure, null) is { } failure)
            {
                throw failure;
            }

            builder.ReserveRevisions(projectionRevisionCapacity);
        }

        internal LifecycleJournal CompleteAfterPreflight()
        {
            builder.ReleaseReservedRevisionCapacity();
            _currentJournal = null;
            return new LifecycleJournal(builder.Entries.ToImmutableArray());
        }

        internal LifecycleJournal Complete()
        {
            PreflightCompletion();
            return CompleteAfterPreflight();
        }

        public void Dispose()
        {
            if (ReferenceEquals(_currentJournal, builder))
            {
                builder.ReleaseReservedRevisionCapacity();
                _currentJournal = null;
            }
        }
    }
}
