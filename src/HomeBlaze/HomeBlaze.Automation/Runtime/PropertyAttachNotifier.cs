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
/// methods and attributes, are announced by the change of that write instead. One per context, added with the first
/// registration of a dispatcher for it and kept, since a service cannot be removed from a context; it serves the routes
/// of every dispatcher that routes the changes of the context and does nothing while none does. Runs on the writing
/// thread after the registry registered the property and only enqueues.
/// </summary>
// Before the method properties are added, so that the subject they are added to is known to be attaching.
[RunsAfter(typeof(SubjectRegistry), typeof(SubjectPathResolver))]
[RunsBefore(typeof(MethodPropertyInitializer))]
internal sealed class PropertyAttachNotifier : ILifecycleHandler, IPropertyLifecycleHandler
{
    // The subject that the write on this thread attaches through a property, until the change of that write is routed or
    // another subject attaches.
    [ThreadStatic]
    private static AttachingSubject? _attaching;

    private readonly Lock _lock = new();
    private AutomationChangeDispatcher.ChangeSource[] _sources = [];

    /// <summary>Announces added properties to the routes from now on. Called by the dispatcher under its lock.</summary>
    public void AddSource(AutomationChangeDispatcher.ChangeSource source)
    {
        lock (_lock)
        {
            if (Array.IndexOf(_sources, source) < 0)
            {
                Volatile.Write(ref _sources, [.. _sources, source]);
            }
        }
    }

    /// <summary>Stops announcing added properties to the routes. Called by the dispatcher under its lock.</summary>
    public void RemoveSource(AutomationChangeDispatcher.ChangeSource source)
    {
        lock (_lock)
        {
            Volatile.Write(ref _sources, Array.FindAll(_sources, candidate => !ReferenceEquals(candidate, source)));
        }
    }

    /// <summary>Ends the attach of the subject on this thread, once the change of the write that attached it is routed.</summary>
    public static void EndAttach() => _attaching?.Clear();

    public void HandleLifecycleChange(SubjectLifecycleChange change)
    {
        if (!change.IsContextAttach || Volatile.Read(ref _sources).Length == 0)
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
            (_attaching ??= new AttachingSubject()).Set(change.Subject);
        }
    }

    public void AttachProperty(SubjectPropertyLifecycleChange change)
    {
        // Any other property is attached only with its subject.
        var sources = Volatile.Read(ref _sources);
        if (sources.Length == 0 || !change.Property.Metadata.IsDynamic || _attaching?.Is(change.Subject) == true)
        {
            return;
        }

        foreach (var source in sources)
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
        private bool _isSet;

        public void Set(IInterceptorSubject subject)
        {
            if (_subject is null)
            {
                _subject = new WeakReference<IInterceptorSubject>(subject);
            }
            else
            {
                _subject.SetTarget(subject);
            }

            _isSet = true;
        }

        public void Clear() => _isSet = false;

        public bool Is(IInterceptorSubject subject) =>
            _isSet && _subject is not null && _subject.TryGetTarget(out var target) && ReferenceEquals(target, subject);
    }
}
