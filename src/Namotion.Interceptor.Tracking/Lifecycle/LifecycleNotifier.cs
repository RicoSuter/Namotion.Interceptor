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

    public void RaiseSubjectAttached(SubjectLifecycleChange change, InterceptorExecutor executor) =>
        RecordEvent(true, change);

    public void RaiseSubjectDetaching(SubjectLifecycleChange change) =>
        RecordEvent(false, change);

    public void PublishEdgeRemoved(
        IInterceptorSubject subject, ILifecycleHandler? subjectHandler, SubjectLifecycleChange change) =>
        InvokeRemovedLifecycleHandlers(subject, subjectHandler, change);

    public void InvokeAddedLifecycleHandlers(
        IInterceptorSubject subject, ILifecycleHandler? subjectHandler,
        InterceptorExecutor executor, SubjectLifecycleChange change) =>
        InvokeAddedLifecycleHandlersCore(subject, subjectHandler, executor, change, null);

    internal void InvokePreparedAddedLifecycleHandlers(
        IInterceptorSubject subject, ILifecycleHandler? subjectHandler,
        InterceptorExecutor executor, SubjectLifecycleChange change, Action? prepareChildren) =>
        InvokeAddedLifecycleHandlersCore(subject, subjectHandler, executor, change, prepareChildren);

    private void InvokeAddedLifecycleHandlersCore(
        IInterceptorSubject subject, ILifecycleHandler? subjectHandler, InterceptorExecutor executor,
        SubjectLifecycleChange change, Action? prepareChildren)
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
        IInterceptorSubject subject, ILifecycleHandler? subjectHandler, SubjectLifecycleChange change)
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
        InterceptorExecutor executor, IEnumerable<SubjectPropertyMetadata> properties,
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
        var change = new SubjectPropertyLifecycleChange(property.Subject, property)
        {
            Context = context,
            Revision = NextProjectionRevision(),
            Metadata = state.Owned[property.Subject].Properties.First(metadata => metadata.Name == property.Name),
            Children = ToChildren(snapshot),
            ChildSubjects = ToSubjectProjections(snapshot, state)
        };
        foreach (var handler in GetPropertyHandlers())
        {
            Record(() => handler.RefreshCollectionProperty(change));
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
        while (true)
        {
            var current = Volatile.Read(ref _projectionRevision);
            if (current < 0 || current == long.MaxValue)
            {
                throw new InvalidOperationException(
                    "The lifecycle projection revision space is exhausted; publication cannot continue safely.");
            }

            var next = current + 1;
            if (Interlocked.CompareExchange(ref _projectionRevision, next, current) == current)
            {
                return next;
            }
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

    private ImmutableArray<ILifecycleHandler> GetLifecycleHandlers() =>
        CurrentBuilder?.LifecycleHandlers ?? context.GetServices<ILifecycleHandler>();

    private ImmutableArray<IPropertyLifecycleHandler> GetPropertyHandlers() =>
        CurrentBuilder?.PropertyHandlers ?? context.GetServices<IPropertyLifecycleHandler>();

    private JournalBuilder? CurrentBuilder =>
        _currentJournal is { Owner: var owner } builder && ReferenceEquals(owner, this) ? builder : null;

    internal int JournalEntryCount => CurrentBuilder?.Entries.Count ?? -1;

    internal List<Action>? DeferJournalEntriesFrom(int start)
    {
        var entries = CurrentBuilder?.Entries;
        if (entries is null || entries.Count == start)
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
            CurrentBuilder!.Entries.AddRange(entries);
        }
    }

    private void RecordEvent(bool attach, SubjectLifecycleChange change)
    {
        var handlers = CurrentBuilder is { } builder
            ? attach ? builder.SubjectAttachedHandlers : builder.SubjectDetachingHandlers
            : GetEventHandlers(attach ? SubjectAttached : SubjectDetaching);
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

        if (CurrentBuilder is { } builder)
        {
            builder.Entries.Add(Invoke);
        }
        else
        {
            Invoke();
        }
    }

    internal sealed record JournalBuilder(
        LifecycleNotifier Owner,
        ImmutableArray<ILifecycleHandler> LifecycleHandlers,
        ImmutableArray<IPropertyLifecycleHandler> PropertyHandlers,
        ImmutableArray<Action<SubjectLifecycleChange>> SubjectAttachedHandlers,
        ImmutableArray<Action<SubjectLifecycleChange>> SubjectDetachingHandlers)
    {
        internal List<Action> Entries { get; } = [];
    }

    internal readonly struct JournalCapture(JournalBuilder builder) : IDisposable
    {
        internal LifecycleJournal Complete()
        {
            if (Interlocked.Exchange(ref builder.Owner._journalCompletionFailure, null) is { } failure)
            {
                throw failure;
            }

            _currentJournal = null;
            return new LifecycleJournal(builder.Entries.ToImmutableArray());
        }

        public void Dispose()
        {
            if (ReferenceEquals(_currentJournal, builder))
            {
                _currentJournal = null;
            }
        }
    }
}
