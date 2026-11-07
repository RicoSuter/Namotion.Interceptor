using System.Collections.Immutable;
using HomeBlaze.Abstractions;
using HomeBlaze.Services;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Attributes;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// Records the graph route of a path by walking it as <see cref="SubjectPathResolver"/> resolves a canonical path for
/// <see cref="Expressions.AutomationPaths"/>, and of the canonical path of an automation as
/// <see cref="SubjectPathResolver.GetPath"/> builds it. Returns what the walk found, so that a caller can compare it with
/// the resolver. A step whose route it cannot tell exactly marks the route unbounded. Its prefixes compare as the resolver
/// compares them, with the current culture. Used by the worker only.
/// </summary>
/// <remarks>
/// A step up from a subject depends on its parents. Its route has the properties that hold the subject, which lose it,
/// but the property that gains it next is not known; the step is exact only for a subject that tells the worker when it
/// gains a parent: the automation of the worker, and a rule whose only parent is that automation.
/// </remarks>
internal sealed class GraphRouteWalker(SubjectPathResolver resolver, IInterceptorSubject? automation)
{
    private readonly HashSet<IInterceptorSubject> _visited = new(ReferenceEqualityComparer.Instance);

    /// <summary>Walks the path to the property that <see cref="Expressions.AutomationPaths.ResolveProperty"/> resolves.</summary>
    public RegisteredSubjectProperty? WalkProperty(string path, IInterceptorSubject rule, GraphRouteBuilder route)
    {
        var lastSeparator = path.LastIndexOf('/');
        var name = lastSeparator < 0 ? path : path[(lastSeparator + 1)..];
        if (name.Length == 0)
        {
            return null;
        }

        var ownerPath = lastSeparator == 0 ? "/" : path[..Math.Max(lastSeparator, 0)];
        var owner = lastSeparator < 0 ? rule : WalkSubject(ownerPath, rule, route);
        if (owner is null)
        {
            return null;
        }

        if (owner.TryGetRegisteredSubject() is not { } registered)
        {
            route.MarkUnbounded();
            return null;
        }

        // A property that is missing or may not be read becomes readable by being added or by gaining its metadata, a
        // change of the property itself.
        var property = registered.TryGetProperty(name);
        if (property is null || !property.IsDisplayable())
        {
            route.Add(new PropertyReference(owner, name));
            return null;
        }

        return property;
    }

    /// <summary>Walks the path to the subject that <see cref="Expressions.AutomationPaths.ResolveSubject"/> resolves.</summary>
    public IInterceptorSubject? WalkSubject(string path, IInterceptorSubject rule, GraphRouteBuilder route)
    {
        if (path == "." || path.Length == 0)
        {
            return rule;
        }

        if (path.StartsWith("/", StringComparison.CurrentCulture))
        {
            var root = resolver.ResolveSubject("/", PathStyle.Canonical);
            if (root is null)
            {
                route.MarkUnbounded();
                return null;
            }

            return path.Length == 1 ? root : WalkSegments(root, path[1..], route);
        }

        if (path.StartsWith("./", StringComparison.CurrentCulture))
        {
            return WalkSegments(rule, path[2..], route);
        }

        return path.StartsWith("../", StringComparison.CurrentCulture) || path == ".."
            ? WalkUp(path, rule, route)
            : WalkSegments(rule, path, route);
    }

    /// <summary>Records the properties that the canonical path of the automation is built from.</summary>
    public void WalkLocation(IInterceptorSubject subject, GraphRouteBuilder route)
    {
        var root = resolver.ResolveSubject("/", PathStyle.Canonical);
        if (ReferenceEquals(subject, root))
        {
            return;
        }

        if (subject.TryGetRegisteredSubject() is not { } registered)
        {
            route.MarkUnbounded();
            return;
        }

        // Every parent of the subject gives a path, the shortest of which counts, and every ancestor its first parent.
        foreach (var parent in registered.Parents)
        {
            WalkAncestors(subject, parent, root, route);
        }
    }

    private void WalkAncestors(IInterceptorSubject subject, SubjectPropertyParent parent, IInterceptorSubject? root, GraphRouteBuilder route)
    {
        _visited.Clear();
        _visited.Add(subject);
        while (true)
        {
            route.Add(parent.Property.Reference);
            var ancestor = parent.Property.Subject;
            if (ReferenceEquals(ancestor, root) || !_visited.Add(ancestor))
            {
                return;
            }

            // A first parent stays first when the ancestor gains another one, but an ancestor without parents gains a
            // first one through a property not on the route.
            if (ancestor.TryGetRegisteredSubject() is not { Parents: { Length: > 0 } parents })
            {
                route.MarkUnbounded();
                return;
            }

            parent = parents[0];
        }
    }

    private IInterceptorSubject? WalkUp(string path, IInterceptorSubject rule, GraphRouteBuilder route)
    {
        var current = rule;
        var remaining = path;
        while (remaining.StartsWith("../", StringComparison.CurrentCulture) || remaining == "..")
        {
            remaining = remaining[(remaining.StartsWith("../", StringComparison.CurrentCulture) ? 3 : 2)..];
            if (WalkParent(current, route) is not { } parent)
            {
                return null;
            }

            current = parent;
        }

        return remaining.Length == 0 ? current : WalkSegments(current, remaining, route);
    }

    private IInterceptorSubject? WalkParent(IInterceptorSubject subject, GraphRouteBuilder route)
    {
        if (subject.TryGetRegisteredSubject() is not { } registered)
        {
            route.MarkUnbounded();
            return null;
        }

        var parents = registered.Parents;
        route.AddHolders(parents);

        // A subject with several parents has no single parent, also after it gains another one.
        if (parents.Length > 1)
        {
            return null;
        }

        if (!TellsParentGains(subject, parents))
        {
            route.MarkUnbounded();
        }

        return parents.Length == 1 ? parents[0].Property.Subject : null;
    }

    private bool TellsParentGains(IInterceptorSubject subject, ImmutableArray<SubjectPropertyParent> parents)
    {
        if (automation is null)
        {
            return false;
        }

        return ReferenceEquals(subject, automation) ||
            subject is AutomationRule && parents.Length == 1 && ReferenceEquals(parents[0].Property.Subject, automation);
    }

    private static IInterceptorSubject? WalkSegments(IInterceptorSubject start, string path, GraphRouteBuilder route)
    {
        if (path.Length == 0)
        {
            return start;
        }

        if (start.Context.TryGetService<ISubjectRegistry>() is not { } registry)
        {
            route.MarkUnbounded();
            return null;
        }

        IInterceptorSubject? current = start;
        foreach (var escaped in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = WalkSegment(registry, current, Uri.UnescapeDataString(escaped), route);
            if (current is null)
            {
                return null;
            }
        }

        return current;
    }

    private static IInterceptorSubject? WalkSegment(ISubjectRegistry registry, IInterceptorSubject subject, string segment, GraphRouteBuilder route)
    {
        if (registry.TryGetRegisteredSubject(subject) is not { } registered)
        {
            route.MarkUnbounded();
            return null;
        }

        var bracketStart = segment.IndexOf('[');
        var hasIndex = bracketStart >= 0 && segment.EndsWith(']');
        var name = hasIndex ? segment[..bracketStart] : segment;
        var property = registered.TryGetProperty(name);
        if (property is null)
        {
            // Added later as a dynamic property, it would take precedence over a child of [InlinePaths].
            route.Add(new PropertyReference(subject, name));
        }

        if (property is not { CanContainSubjects: true })
        {
            return WalkInlinePaths(registered, subject, segment, route);
        }

        route.Add(property.Reference);
        if (property.GetValue() is not { } value)
        {
            return null;
        }

        if (property.IsSubjectReference)
        {
            return value as IInterceptorSubject;
        }

        return hasIndex ? SubjectPathResolverExtensions.FindChildByIndex(property, segment[(bracketStart + 1)..^1]) : null;
    }

    private static IInterceptorSubject? WalkInlinePaths(RegisteredSubject registered, IInterceptorSubject subject, string segment, GraphRouteBuilder route)
    {
        if (InlinePathsAttribute.GetInlinePathsPropertyName(subject.GetType()) is not { } childrenName)
        {
            return null;
        }

        if (registered.TryGetProperty(childrenName) is not { } children)
        {
            route.Add(new PropertyReference(subject, childrenName));
            return null;
        }

        route.Add(children.Reference);
        return SubjectPathResolverExtensions.FindChildByIndex(children, segment);
    }
}
