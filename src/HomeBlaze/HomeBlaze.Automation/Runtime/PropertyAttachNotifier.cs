using HomeBlaze.Abstractions;
using HomeBlaze.Automation.Expressions;
using HomeBlaze.Services;
using HomeBlaze.Services.Lifecycle;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Attributes;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// Announces a property that is added to a subject at run time, such as a dynamic property or an attribute, to the
/// automations of a context as a graph change of that property, and an attribute also under the name a route records it
/// with, since a path that led nowhere or through an [InlinePaths] child may lead to it now. An attribute that decides
/// whether a path is readable is also announced under that name on the subject of each parent of its subject. The
/// properties a subject is attached with by the write of a property that holds subjects, such as its methods and
/// attributes, are announced by the change of that write instead. Also tells the registration of an automation when the
/// automation or one of its rules gains a parent, see <see cref="AutomationChangeDispatcher.Registration.OnParentGained"/>.
/// One per context, added with the first registration of a dispatcher for it and kept, since a service cannot be removed
/// from a context; it serves the routes of every dispatcher that routes the changes of the context and does nothing while
/// none does. Runs on the writing thread after the registry registered the property and only enqueues or marks.
/// </summary>
// Before the method properties are added, so that the subject they are added to is known to be attaching.
[RunsAfter(typeof(SubjectRegistry), typeof(SubjectPathResolver))]
[RunsBefore(typeof(MethodPropertyInitializer))]
internal sealed class PropertyAttachNotifier(ISubjectRegistry? registry) : ILifecycleHandler, IPropertyLifecycleHandler
{
    // The subject that the write on this thread attaches through a property, until the change of that write is routed or
    // another subject attaches.
    [ThreadStatic]
    private static AttachingSubject? _attaching;

    private readonly Lock _lock = new();
    private AutomationChangeDispatcher.ChangeSource[] _sources = [];
    private volatile bool _isPublished;

    /// <summary>
    /// Whether every context that falls back on the context of the notifier sees it; set by the dispatcher once, after
    /// the publication was fenced on every processor.
    /// </summary>
    public bool IsPublished
    {
        get => _isPublished;
        set => _isPublished = value;
    }

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
        if (Volatile.Read(ref _sources).Length == 0)
        {
            return;
        }

        if (change.IsPropertyReferenceAdded)
        {
            AddParentGains(change.Subject);
        }

        if (!change.IsContextAttach || registry is null)
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
            (_attaching ??= new AttachingSubject()).Set(change.Subject, registry.StructureVersion);
        }
    }

    public void AttachProperty(SubjectPropertyLifecycleChange change)
    {
        // Any other property is attached only with its subject.
        var sources = Volatile.Read(ref _sources);
        if (sources.Length == 0 || !change.Property.Metadata.IsDynamic ||
            (registry is not null && _attaching?.TryContinue(change.Subject, registry.StructureVersion) == true))
        {
            return;
        }

        if (change.Property.TryGetRegisteredProperty() is { IsAttribute: true } attribute)
        {
            AnnounceAttribute(sources, change.Property, attribute);
            return;
        }

        foreach (var source in sources)
        {
            source.OnPropertyAdded(change.Property);
        }
    }

    private static void AnnounceAttribute(
        AutomationChangeDispatcher.ChangeSource[] sources, PropertyReference property, RegisteredSubjectProperty attribute)
    {
        // An attribute is found by the property it belongs to whatever its own name, and a route records it under the
        // name that AutomationPaths gives it.
        var metadata = attribute.AttributeMetadata;
        var name = AutomationPaths.GetAttributePropertyName(metadata.PropertyName, metadata.AttributeName);
        var parents = DecidesReadability(metadata) ? attribute.Parent.Parents : [];
        foreach (var source in sources)
        {
            source.OnPropertyAdded(property);
            if (name != attribute.Name)
            {
                source.OnPropertyAdded(new PropertyReference(attribute.Subject, name));
            }

            foreach (var parent in parents)
            {
                source.OnPropertyAdded(new PropertyReference(parent.Property.Subject, name));
            }
        }
    }

    // A route that reached the subject through one of its parents records such an attribute on the subject of that
    // parent, see AutomationPaths.ResolveProperty.
    private static bool DecidesReadability(PropertyAttributeAttribute metadata) =>
        metadata.AttributeName is KnownAttributes.Configuration or KnownAttributes.State;

    // A path from a rule or up from an automation depends on its parents, and the property that adds a parent is on no
    // route.
    private static void AddParentGains(IInterceptorSubject subject)
    {
        if (subject is Automation automation)
        {
            AddParentGain(automation);
        }
        else if (subject is AutomationRule && subject.TryGetRegisteredSubject() is { } registered)
        {
            foreach (var parent in registered.Parents)
            {
                if (parent.Property.Subject is Automation owner)
                {
                    AddParentGain(owner);
                }
            }
        }
    }

    private static void AddParentGain(Automation automation) => automation.Runtime?.Changes.Registration?.OnParentGained();

    // A property is detached only with its subject, whose write is a graph change already.
    public void DetachProperty(SubjectPropertyLifecycleChange change)
    {
    }

    // Weak, so that the subject that attached last on a thread is not kept.
    private sealed class AttachingSubject
    {
        private WeakReference<IInterceptorSubject>? _subject;
        private long _structureVersion;
        private bool _isSet;

        public void Set(IInterceptorSubject subject, long structureVersion)
        {
            if (_subject is null)
            {
                _subject = new WeakReference<IInterceptorSubject>(subject);
            }
            else
            {
                _subject.SetTarget(subject);
            }

            _structureVersion = structureVersion;
            _isSet = true;
        }

        public void Clear() => _isSet = false;

        // A property belongs to the attach while every structural change since the mark was a property the attach added,
        // which the registry counts with two each before this runs. Any other change, also on another thread, ends the
        // mark, so that a mark that a failed write left behind hides a later property only while nothing else changed.
        public bool TryContinue(IInterceptorSubject subject, long structureVersion)
        {
            if (!_isSet || _subject is null || !_subject.TryGetTarget(out var target) || !ReferenceEquals(target, subject))
            {
                return false;
            }

            if (structureVersion != _structureVersion + 2)
            {
                _isSet = false;
                return false;
            }

            _structureVersion = structureVersion;
            return true;
        }
    }
}
