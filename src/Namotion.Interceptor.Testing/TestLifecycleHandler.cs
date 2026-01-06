using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Testing;

/// <summary>
/// Helper for capturing lifecycle events in a consistent snapshot format.
/// Subject lifecycle format: "+ Parent.Property -> Subject (refs: N, attached)" or "- Parent.Property -> Subject (refs: N, detached)"
/// Property lifecycle format: "prop+ Subject.PropertyName" or "prop- Subject.PropertyName"
/// Uses ToString() on subjects for naming - ensure your test models override ToString().
/// </summary>
public class TestLifecycleHandler : ILifecycleHandler, IPropertyLifecycleHandler
{
    private readonly List<string> _events = [];
    private readonly bool _trackProperties;

    public TestLifecycleHandler(bool trackProperties = false)
    {
        _trackProperties = trackProperties;
    }

    public void HandleLifecycleChange(SubjectLifecycleChange change)
    {
        var subject = GetSubjectName(change.Subject);

        if (change.IsContextAttach && change.Property is null)
        {
            // Root subject attached directly to context
            _events.Add($"+ {subject} (attached, refs: {change.ReferenceCount})");
        }
        else if (change.IsPropertyReferenceAdded)
        {
            // Subject attached via property reference
            var property = FormatProperty(change);
            var attached = change.IsContextAttach ? ", attached" : "";
            _events.Add($"+ {property} -> {subject} (refs: {change.ReferenceCount}{attached})");
        }
        else if (change.IsPropertyReferenceRemoved)
        {
            // Subject detached from property reference
            var property = FormatProperty(change);
            var detached = change.IsContextDetach ? ", detached" : "";
            _events.Add($"- {property} -> {subject} (refs: {change.ReferenceCount}{detached})");
        }
        else if (change.IsContextDetach && change.Property is null)
        {
            // Root subject detached from context
            _events.Add($"- {subject} (detached, refs: {change.ReferenceCount})");
        }
    }

    public void AttachProperty(SubjectPropertyLifecycleChange change)
    {
        if (!_trackProperties)
            return;

        var subject = GetSubjectName(change.Subject);
        _events.Add($"prop+ {subject}.{change.Property.Name}");
    }

    public void DetachProperty(SubjectPropertyLifecycleChange change)
    {
        if (!_trackProperties)
            return;

        var subject = GetSubjectName(change.Subject);
        _events.Add($"prop- {subject}.{change.Property.Name}");
    }

    public string[] GetEvents() => [.. _events];

    public void Clear() => _events.Clear();

    private static string FormatProperty(SubjectLifecycleChange change)
    {
        var parent = GetSubjectName(change.Property!.Value.Subject);
        var name = change.Property.Value.Name;
        var index = change.Index is not null ? $"[{change.Index}]" : "";
        return $"{parent}.{name}{index}";
    }

    private static string GetSubjectName(IInterceptorSubject subject)
    {
        var name = subject.ToString();
        // Fall back to type name if ToString() returns default or empty
        if (string.IsNullOrWhiteSpace(name) || name == subject.GetType().FullName)
        {
            return subject.GetType().Name;
        }
        return name;
    }
}
