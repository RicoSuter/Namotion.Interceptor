using Namotion.Interceptor.Registry.Tests.Models;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Registry.Tests.GraphBehavior;

/// <summary>
/// Helper for capturing lifecycle events in a consistent snapshot format.
/// Format: "+ Property -> Subject (refs: N, attached)" or "- Property -> Subject (refs: N, detached)"
/// </summary>
public class GraphTestHelper : ILifecycleHandler
{
    private readonly List<string> _events = [];

    public void OnLifecycleEvent(SubjectLifecycleChange change)
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
            var prop = FormatProperty(change);
            var attached = change.IsContextAttach ? ", attached" : "";
            _events.Add($"+ {prop} -> {subject} (refs: {change.ReferenceCount}{attached})");
        }
        else if (change.IsPropertyReferenceRemoved)
        {
            // Subject detached from property reference
            var prop = FormatProperty(change);
            var detached = change.IsContextDetach ? ", detached" : "";
            _events.Add($"- {prop} -> {subject} (refs: {change.ReferenceCount}{detached})");
        }
        else if (change.IsContextDetach && change.Property is null)
        {
            // Root subject detached from context
            _events.Add($"- {subject} (detached, refs: {change.ReferenceCount})");
        }
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
        => subject switch
        {
            Person p => p.FirstName ?? "Person",
            _ => subject.GetType().Name
        };
}
