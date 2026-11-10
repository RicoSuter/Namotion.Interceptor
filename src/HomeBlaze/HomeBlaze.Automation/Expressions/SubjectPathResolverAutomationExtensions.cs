using System.Runtime.CompilerServices;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Automation.Runtime;
using HomeBlaze.Services;
using Namotion.Interceptor;
using Namotion.Interceptor.Expressions;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Automation.Expressions;

/// <summary>
/// Resolves the paths of automation expressions and actions. An absolute path starts at the root, any other path at the
/// rule, so <c>./LastTriggeredAt</c> is a property of the rule and <c>../CurrentState</c> one of its automation. A
/// resolution reports its graph route to the route builder it is given, see <see cref="ISubjectPathRecorder"/>.
/// </summary>
internal static class SubjectPathResolverAutomationExtensions
{
    // The names that the attributes of a property have on the routes, see GetAttributePropertyName, kept with the
    // property so that recording them again allocates nothing.
    private static readonly ConditionalWeakTable<RegisteredSubjectProperty, string> ConfigurationNames = new();
    private static readonly ConditionalWeakTable<RegisteredSubjectProperty, string> StateNames = new();
    private static readonly ConditionalWeakTable<RegisteredSubjectProperty, string>.CreateValueCallback CreateConfigurationName =
        property => GetAttributePropertyName(property.Name, KnownAttributes.Configuration);
    private static readonly ConditionalWeakTable<RegisteredSubjectProperty, string>.CreateValueCallback CreateStateName =
        property => GetAttributePropertyName(property.Name, KnownAttributes.State);

    // The subject path and the name of each path, kept with the path, since an expression resolves the same path
    // instances on each evaluation.
    private static readonly ConditionalWeakTable<string, PathParts> Parts = new();
    private static readonly ConditionalWeakTable<string, PathParts>.CreateValueCallback CreateParts = PathParts.Create;

    /// <summary>
    /// Gets the name under which a route records the attribute of a property, the name that
    /// <see cref="RegisteredSubjectProperty.AddAttribute"/> gives it; an attribute added under another name is announced
    /// under this one too, see <see cref="Runtime.PropertyAttachNotifier"/>.
    /// </summary>
    public static string GetAttributePropertyName(string propertyName, string attributeName) => $"{propertyName}@{attributeName}";

    /// <summary>
    /// Resolves the property a path ends at, as <see cref="ResolveTarget"/> does, when an expression may read it: a secret
    /// or excluded property resolves to null.
    /// </summary>
    public static RegisteredSubjectProperty? ResolveProperty(this SubjectPathResolver resolver, string path, AutomationRule rule, GraphRouteBuilder? recorder = null)
    {
        var property = resolver.ResolveTarget(path, rule, recorder);
        return property is not null && IsReadable(property, recorder) ? property : null;
    }

    private static bool IsReadable(RegisteredSubjectProperty property, GraphRouteBuilder? recorder)
    {
        var isReadable = property.IsDisplayable();
        if (recorder is null)
        {
            return isReadable;
        }

        // Added at run time, a secret configuration hides the property and state metadata shows a plain one.
        var configuration = property.TryGetAttribute(KnownAttributes.Configuration);
        if (configuration is null)
        {
            AddAttribute(property, ConfigurationNames, CreateConfigurationName, recorder);
        }

        if (!isReadable && configuration?.GetValue() is not ConfigurationMetadata { IsSecret: true } &&
            property.TryGetAttribute(KnownAttributes.State) is null)
        {
            AddAttribute(property, StateNames, CreateStateName, recorder);
        }

        return isReadable;
    }

    // A route that reached the subject through one of its parents records the attribute on the subject of that parent,
    // which stays the same when another subject takes the place of this one; the notifier announces the attribute on the
    // subject of each parent. The subject loses that parent by a write of it or the detach of the subject that holds it,
    // which the path passed. Without such a parent, as through a plain or derived property, the subject can lose all its
    // parents off the route.
    private static void AddAttribute(
        RegisteredSubjectProperty property,
        ConditionalWeakTable<RegisteredSubjectProperty, string> names,
        ConditionalWeakTable<RegisteredSubjectProperty, string>.CreateValueCallback createName,
        GraphRouteBuilder recorder)
    {
        var owner = FindParentOnRoute(property.Parent, recorder) ?? property.Subject;
        recorder.AddProperty(new PropertyReference(owner, names.GetValue(property, createName)));
    }

    private static IInterceptorSubject? FindParentOnRoute(RegisteredSubject subject, GraphRouteBuilder recorder)
    {
        foreach (var parent in subject.Parents)
        {
            if (recorder.Contains(parent.Property.Reference))
            {
                return parent.Property.Subject;
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves the property or operation a path ends at: the last segment names it, the part before it leads to its subject.
    /// </summary>
    public static RegisteredSubjectProperty? ResolveTarget(this SubjectPathResolver resolver, string path, AutomationRule rule, GraphRouteBuilder? recorder = null)
    {
        var lastSeparator = path.LastIndexOf(SubjectPathResolver.Separator);
        if (lastSeparator == path.Length - 1)
        {
            return null;
        }

        if (lastSeparator < 0)
        {
            AddRuleParents(rule, recorder);
            return FindProperty(rule, path, recorder);
        }

        var parts = Parts.GetValue(path, CreateParts);
        var owner = resolver.ResolveSubject(parts.SubjectPath, rule, recorder);
        return owner is null ? null : FindProperty(owner, parts.Name, recorder);
    }

    private static RegisteredSubjectProperty? FindProperty(IInterceptorSubject owner, string name, GraphRouteBuilder? recorder)
    {
        if (owner.TryGetRegisteredSubject() is not { } registered)
        {
            recorder?.MarkUnbounded();
            return null;
        }

        var property = registered.TryGetProperty(name);
        if (property is null)
        {
            // Added later as a dynamic property.
            recorder?.AddProperty(new PropertyReference(owner, name));
        }

        return property;
    }

    /// <summary>Resolves a subject; a path that leads nowhere or is malformed gives null.</summary>
    public static IInterceptorSubject? ResolveSubject(this SubjectPathResolver resolver, string path, AutomationRule rule, GraphRouteBuilder? recorder = null)
    {
        if (!path.StartsWith(SubjectPathResolver.Separator))
        {
            AddRuleParents(rule, recorder);
        }

        return ResolveSubjectFrom(resolver, path, rule, recorder);
    }

    private static IInterceptorSubject? ResolveSubjectFrom(SubjectPathResolver resolver, string path, IInterceptorSubject start, GraphRouteBuilder? recorder)
    {
        if (path == ".")
        {
            return start;
        }

        try
        {
            return resolver.ResolveSubject(path, PathStyle.Canonical, start, recorder);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Written by the author of a rule: a malformed path leads nowhere instead of failing the automation.
            recorder?.MarkUnbounded();
            return null;
        }
    }

    // The resolver leaves out the attachment of the subject a relative path starts at, and a rule is detached and
    // attached again by a write of the property that holds it.
    private static void AddRuleParents(AutomationRule rule, GraphRouteBuilder? recorder)
    {
        if (recorder is null)
        {
            return;
        }

        if (rule.TryGetRegisteredSubject() is { } registered)
        {
            recorder.AddParentStep(rule, registered.Parents);
        }
        else
        {
            recorder.MarkUnbounded();
        }
    }

    /// <summary>Reads the property at a path that starts at the subject, when an expression may read it.</summary>
    public static ExpressionValue ReadFrom(this SubjectPathResolver resolver, string path, IInterceptorSubject subject)
    {
        var property = ResolveFrom(resolver, path, subject);
        return property is not null && IsReadable(property, recorder: null)
            ? ExpressionValue.FromProperty(property, property.GetValue())
            : ExpressionValue.Missing;
    }

    // Not through the split cache of ResolveTarget: the callers cut a new string from the expression on each evaluation,
    // which would only add an entry to it every time.
    private static RegisteredSubjectProperty? ResolveFrom(SubjectPathResolver resolver, string path, IInterceptorSubject subject)
    {
        var start = path.StartsWith("./", StringComparison.Ordinal) ? 2 : 0;
        var lastSeparator = path.LastIndexOf(SubjectPathResolver.Separator);
        if (lastSeparator == path.Length - 1)
        {
            return null;
        }

        if (lastSeparator < start)
        {
            return FindProperty(subject, start == 0 ? path : path[start..], recorder: null);
        }

        var subjectPath = start == 0 ? string.Concat("./", path.AsSpan(0, lastSeparator)) : path[..lastSeparator];
        var owner = ResolveSubjectFrom(resolver, subjectPath, subject, recorder: null);
        return owner is null ? null : FindProperty(owner, path[(lastSeparator + 1)..], recorder: null);
    }

    private sealed class PathParts(string subjectPath, string name)
    {
        public string SubjectPath { get; } = subjectPath;

        public string Name { get; } = name;

        public static PathParts Create(string path)
        {
            var lastSeparator = path.LastIndexOf(SubjectPathResolver.Separator);
            return new PathParts(lastSeparator == 0 ? "/" : path[..lastSeparator], path[(lastSeparator + 1)..]);
        }
    }
}
