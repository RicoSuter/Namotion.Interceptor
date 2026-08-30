using System.Collections.Immutable;

namespace Namotion.Interceptor.Tracking.Lifecycle;

internal sealed class LifecycleJournal(
    PropertyReference property,
    long revision,
    ImmutableArray<Action> entries)
{
    internal PropertyReference Property { get; } = property;
    internal long Revision { get; } = revision;

    internal Exception? Drain(Exception? primaryException)
    {
        List<Exception>? failures = primaryException is null ? null : [primaryException];
        foreach (var entry in entries)
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

internal sealed class LifecycleNotifier(
    IInterceptorSubjectContext context,
    LifecycleInterceptor originatingLifecycle)
{
    [ThreadStatic]
    private static JournalBuilder? _currentJournal;

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
            context.GetServices<IPropertyLifecycleHandler>());
        return new JournalCapture(_currentJournal);
    }

    public void RaiseSubjectAttached(SubjectLifecycleChange change) =>
        RecordEvent(SubjectAttached, change, change.Subject);

    public void RaiseSubjectDetaching(SubjectLifecycleChange change) =>
        RecordEvent(SubjectDetaching, change);

    public void PublishEdgeRemoved(
        IInterceptorSubject subject,
        PropertyReference property,
        object? index,
        int referenceCount) =>
        InvokeRemovedLifecycleHandlers(subject, new SubjectLifecycleChange
        {
            Subject = subject,
            Property = property,
            Index = index,
            ReferenceCount = referenceCount,
            IsPropertyReferenceRemoved = true
        });

    public void InvokeAddedLifecycleHandlers(
        IInterceptorSubject subject,
        SubjectLifecycleChange change,
        Dictionary<IInterceptorSubject, Interceptors.OwnershipReservationToken>? reservations = null)
    {
        foreach (var handler in GetLifecycleHandlers())
        {
            if (ReferenceEquals(handler, originatingLifecycle))
            {
                originatingLifecycle.HandleLifecycleChange(change, reservations);
            }
            else
            {
                Record(() => handler.HandleLifecycleChange(change),
                    attachedSubject: change.IsContextAttach ? subject : null);
            }
        }

        if (subject is ILifecycleHandler subjectHandler)
        {
            Record(() => subjectHandler.HandleLifecycleChange(change),
                attachedSubject: change.IsContextAttach ? subject : null);
        }
    }

    public void InvokeRemovedLifecycleHandlers(IInterceptorSubject subject, SubjectLifecycleChange change)
    {
        if (subject is ILifecycleHandler subjectHandler)
        {
            Record(() => subjectHandler.HandleLifecycleChange(change));
        }

        foreach (var handler in GetLifecycleHandlers())
        {
            Record(() => handler.HandleLifecycleChange(change));
        }
    }

    public void AttachSubjectProperties(IInterceptorSubject subject, IEnumerable<string> propertyNames) =>
        RecordProperties(subject, propertyNames, attach: true);

    public void DetachSubjectProperties(IInterceptorSubject subject, IEnumerable<string> propertyNames) =>
        RecordProperties(subject, propertyNames, attach: false);

    private void RecordProperties(IInterceptorSubject subject, IEnumerable<string> propertyNames, bool attach)
    {
        foreach (var name in propertyNames)
        {
            var change = new SubjectPropertyLifecycleChange(subject, new PropertyReference(subject, name));
            foreach (var handler in GetPropertyHandlers())
            {
                Record(
                    () => InvokeProperty(handler, change, attach),
                    propertyCallback: true,
                    attachedSubject: attach ? subject : null);
            }

            if (subject is IPropertyLifecycleHandler subjectHandler)
            {
                Record(
                    () => InvokeProperty(subjectHandler, change, attach),
                    propertyCallback: true,
                    attachedSubject: attach ? subject : null);
            }
        }
    }

    private static void InvokeProperty(IPropertyLifecycleHandler handler, SubjectPropertyLifecycleChange change, bool attach)
    {
        Action<SubjectPropertyLifecycleChange> callback = attach ? handler.AttachProperty : handler.DetachProperty;
        callback(change);
    }

    public void RefreshCollectionProperty(PropertyReference property, object? value)
    {
        foreach (var handler in GetPropertyHandlers())
        {
            Record(() => handler.RefreshCollectionProperty(property, value));
        }
    }

    private ImmutableArray<ILifecycleHandler> GetLifecycleHandlers() =>
        CurrentBuilder?.LifecycleHandlers ?? context.GetServices<ILifecycleHandler>();

    private ImmutableArray<IPropertyLifecycleHandler> GetPropertyHandlers() =>
        CurrentBuilder?.PropertyHandlers ?? context.GetServices<IPropertyLifecycleHandler>();

    private JournalBuilder? CurrentBuilder =>
        _currentJournal is { Owner: var owner } builder && ReferenceEquals(owner, this) ? builder : null;

    private void RecordEvent(
        Action<SubjectLifecycleChange>? handlers,
        SubjectLifecycleChange change,
        IInterceptorSubject? attachedSubject = null)
    {
        if (handlers is not null)
        {
            foreach (Action<SubjectLifecycleChange> handler in handlers.GetInvocationList())
            {
                Record(() => handler(change), attachedSubject: attachedSubject);
            }
        }
    }

    private void Record(
        Action action,
        bool propertyCallback = false,
        IInterceptorSubject? attachedSubject = null)
    {
        var expectedRevision = attachedSubject?.Executor.AttachmentRevision;
        bool IsCurrentAttachment() => attachedSubject?.Executor.AttachmentRevision == expectedRevision;

        void Invoke()
        {
            if (attachedSubject is not null && !IsCurrentAttachment())
            {
                return;
            }

            try
            {
                if (propertyCallback)
                {
                    using var scope = CallbackReentrancyGuard.EnterPropertyCallbackScope();
                    action();
                }
                else
                {
                    using var scope = CallbackReentrancyGuard.EnterScope();
                    action();
                }
            }
            catch when (attachedSubject is not null && !IsCurrentAttachment())
            {
            }
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

    internal sealed class JournalBuilder(
        LifecycleNotifier owner,
        ImmutableArray<ILifecycleHandler> lifecycleHandlers,
        ImmutableArray<IPropertyLifecycleHandler> propertyHandlers)
    {
        internal LifecycleNotifier Owner { get; } = owner;
        internal ImmutableArray<ILifecycleHandler> LifecycleHandlers { get; } = lifecycleHandlers;
        internal ImmutableArray<IPropertyLifecycleHandler> PropertyHandlers { get; } = propertyHandlers;
        internal List<Action> Entries { get; } = [];
    }

    internal readonly struct JournalCapture(JournalBuilder builder) : IDisposable
    {
        internal LifecycleJournal Complete(PropertyReference property, long revision)
        {
            _currentJournal = null;
            return new LifecycleJournal(property, revision, builder.Entries.ToImmutableArray());
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
