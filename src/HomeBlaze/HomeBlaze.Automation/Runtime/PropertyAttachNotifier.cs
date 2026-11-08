using HomeBlaze.Services;
using HomeBlaze.Services.Lifecycle;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// Announces a property that is added to a subject at run time, such as a dynamic property or an attribute, to the
/// automations of a context as a graph change, since a path that led nowhere or through an [InlinePaths] child may lead
/// to it now. The properties a subject is attached with by the write of a property that holds subjects, such as its
/// methods and attributes, are announced by the change of that write instead. Added to the context with the first
/// registration of <see cref="Dispatcher"/> for it and kept, since a service cannot be removed from a context; it does
/// nothing while the dispatcher routes no changes of the context. Runs on the writing thread after the registry
/// registered the property and only enqueues.
/// </summary>
// Before the method properties are added, so that the subject they are added to is known to be attaching.
[RunsAfter(typeof(SubjectRegistry), typeof(SubjectPathResolver))]
[RunsBefore(typeof(MethodPropertyInitializer))]
internal sealed class PropertyAttachNotifier(AutomationChangeDispatcher dispatcher) : ILifecycleHandler, IPropertyLifecycleHandler
{
    // The subject that the write on this thread attaches through a property, until the change of that write is routed or
    // another subject attaches.
    [ThreadStatic]
    private static AttachingSubject? _attaching;

    private AutomationChangeDispatcher.ChangeSource? _source;

    public AutomationChangeDispatcher Dispatcher { get; } = dispatcher;

    /// <summary>The routes of the change stream of the context, or null while none are; set by the dispatcher under its lock.</summary>
    internal AutomationChangeDispatcher.ChangeSource? Source
    {
        get => Volatile.Read(ref _source);
        set => Volatile.Write(ref _source, value);
    }

    /// <summary>Ends the attach of the subject on this thread, once the change of the write that attached it is routed.</summary>
    public static void EndAttach() => _attaching?.Clear();

    public void HandleLifecycleChange(SubjectLifecycleChange change)
    {
        if (!change.IsContextAttach || Source is not { } source)
        {
            return;
        }

        if (change.Property is null)
        {
            // A subject attached to the context directly, such as a root, has no write whose change follows.
            _attaching?.Clear();
        }
        else
        {
            (_attaching ??= new AttachingSubject()).Set(change.Subject, source.Id);
        }
    }

    public void AttachProperty(SubjectPropertyLifecycleChange change)
    {
        // Any other property is attached only with its subject.
        if (Source is { } source && change.Property.Metadata.IsDynamic && _attaching?.Is(change.Subject, source.Id) != true)
        {
            source.OnPropertyAdded();
        }
    }

    // A property is detached only with its subject, whose write is a graph change already.
    public void DetachProperty(SubjectPropertyLifecycleChange change)
    {
    }

    // Weak, so that the subject that attached last on a thread is not kept.
    private sealed class AttachingSubject
    {
        private WeakReference<IInterceptorSubject>? _subject;
        private long _sourceId;

        public void Set(IInterceptorSubject subject, long sourceId)
        {
            if (_subject is null)
            {
                _subject = new WeakReference<IInterceptorSubject>(subject);
            }
            else
            {
                _subject.SetTarget(subject);
            }

            _sourceId = sourceId;
        }

        public void Clear() => _sourceId = 0;

        public bool Is(IInterceptorSubject subject, long sourceId) =>
            _sourceId == sourceId && _subject is not null && _subject.TryGetTarget(out var target) && ReferenceEquals(target, subject);
    }
}
