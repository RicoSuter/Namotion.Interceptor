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
internal static class AutomationPaths
{
    /// <summary>
    /// Resolves the property a path ends at, as <see cref="ResolveTarget"/> does, when an expression may read it: a secret
    /// or excluded property resolves to null.
    /// </summary>
    public static RegisteredSubjectProperty? ResolveProperty(SubjectPathResolver resolver, string path, IInterceptorSubject rule) =>
        ResolveTarget(resolver, path, rule) is { } property && property.IsDisplayable() ? property : null;

    /// <summary>
    /// Resolves the property or operation a path ends at: the last segment names it, the part before it leads to its subject.
    /// </summary>
    public static RegisteredSubjectProperty? ResolveTarget(SubjectPathResolver resolver, string path, IInterceptorSubject rule)
    {
        var lastSeparator = path.LastIndexOf(SubjectPathResolver.Separator);
        var name = lastSeparator < 0 ? path : path[(lastSeparator + 1)..];
        if (name.Length == 0)
        {
            return null;
        }

        var ownerPath = lastSeparator == 0 ? "/" : path[..Math.Max(lastSeparator, 0)];
        var owner = lastSeparator < 0 ? rule : ResolveSubject(resolver, ownerPath, rule);
        return owner?.TryGetRegisteredProperty(name);
    }

    /// <summary>Resolves a subject; a path that leads nowhere or is malformed gives null.</summary>
    public static IInterceptorSubject? ResolveSubject(SubjectPathResolver resolver, string path, IInterceptorSubject rule)
    {
        if (path == ".")
        {
            return rule;
        }

        try
        {
            return resolver.ResolveSubject(path, PathStyle.Canonical, rule);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Written by the author of a rule: a malformed path leads nowhere instead of failing the automation.
            return null;
        }
    }

    /// <summary>Reads the property at a path that starts at the subject, when an expression may read it.</summary>
    public static ExpressionValue ReadFrom(SubjectPathResolver resolver, string path, IInterceptorSubject subject)
    {
        var relativePath = path.StartsWith("./", StringComparison.Ordinal) ? path : "./" + path;
        var property = ResolveProperty(resolver, relativePath, subject);
        return property is null ? ExpressionValue.Missing : ExpressionValue.FromProperty(property, property.GetValue());
    }
}
