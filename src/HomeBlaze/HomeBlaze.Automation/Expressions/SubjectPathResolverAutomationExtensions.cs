using HomeBlaze.Abstractions;
using HomeBlaze.Services;
using Namotion.Interceptor;
using Namotion.Interceptor.Expressions;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Automation.Expressions;

/// <summary>
/// Resolves the paths of automation expressions and actions. An absolute path starts at the root, any other path at the
/// rule, so <c>./LastTriggeredAt</c> is a property of the rule and <c>../CurrentState</c> one of its automation.
/// </summary>
internal static class SubjectPathResolverAutomationExtensions
{
    /// <summary>
    /// Resolves the property a path ends at, as <see cref="ResolveTarget"/> does, when an expression may read it: a secret
    /// or excluded property resolves to null.
    /// </summary>
    public static RegisteredSubjectProperty? ResolveProperty(this SubjectPathResolver resolver, string path, AutomationRule rule) =>
        ResolvePropertyFrom(resolver, path, rule);

    /// <summary>
    /// Resolves the property or operation a path ends at: the last segment names it, the part before it leads to its subject.
    /// </summary>
    public static RegisteredSubjectProperty? ResolveTarget(this SubjectPathResolver resolver, string path, AutomationRule rule) =>
        ResolveTargetFrom(resolver, path, rule);

    /// <summary>Resolves a subject; a path that leads nowhere or is malformed gives null.</summary>
    public static IInterceptorSubject? ResolveSubject(this SubjectPathResolver resolver, string path, AutomationRule rule) =>
        ResolveSubjectFrom(resolver, path, rule);

    /// <summary>Reads the property at a path that starts at the subject, when an expression may read it.</summary>
    public static ExpressionValue ReadFrom(this SubjectPathResolver resolver, string path, IInterceptorSubject subject)
    {
        var relativePath = path.StartsWith("./", StringComparison.Ordinal) ? path : "./" + path;
        var property = ResolvePropertyFrom(resolver, relativePath, subject);
        return property is null ? ExpressionValue.Missing : ExpressionValue.FromProperty(property, property.GetValue());
    }

    private static RegisteredSubjectProperty? ResolvePropertyFrom(SubjectPathResolver resolver, string path, IInterceptorSubject start) =>
        ResolveTargetFrom(resolver, path, start) is { } property && property.IsDisplayable() ? property : null;

    private static RegisteredSubjectProperty? ResolveTargetFrom(SubjectPathResolver resolver, string path, IInterceptorSubject start)
    {
        var lastSeparator = path.LastIndexOf(SubjectPathResolver.Separator);
        var name = lastSeparator < 0 ? path : path[(lastSeparator + 1)..];
        if (name.Length == 0)
        {
            return null;
        }

        var ownerPath = lastSeparator == 0 ? "/" : path[..Math.Max(lastSeparator, 0)];
        var owner = lastSeparator < 0 ? start : ResolveSubjectFrom(resolver, ownerPath, start);
        return owner?.TryGetRegisteredProperty(name);
    }

    private static IInterceptorSubject? ResolveSubjectFrom(SubjectPathResolver resolver, string path, IInterceptorSubject start)
    {
        if (path == ".")
        {
            return start;
        }

        try
        {
            return resolver.ResolveSubject(path, PathStyle.Canonical, start);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Written by the author of a rule: a malformed path leads nowhere instead of failing the automation.
            return null;
        }
    }
}
