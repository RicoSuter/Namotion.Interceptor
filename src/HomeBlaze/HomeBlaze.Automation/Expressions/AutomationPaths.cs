using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Services;
using Namotion.Interceptor;
using Namotion.Interceptor.Expressions;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Automation.Expressions;

/// <summary>
/// Resolves the paths of automation expressions and actions. An absolute path starts at the root, any other path at the
/// rule, so <c>./LastTriggeredAt</c> is a property of the rule and <c>../CurrentState</c> one of its automation. A
/// resolution reports its graph route to the recorder it is given, see <see cref="ISubjectPathRecorder"/>.
/// </summary>
internal static class AutomationPaths
{
    /// <summary>The character between the segments of a path.</summary>
    public const char Separator = '/';

    /// <summary>
    /// Resolves the property a path ends at, as <see cref="ResolveTarget"/> does, when an expression may read it: a secret
    /// or excluded property resolves to null.
    /// </summary>
    public static RegisteredSubjectProperty? ResolveProperty(SubjectPathResolver resolver, string path, IInterceptorSubject rule, ISubjectPathRecorder? recorder = null)
    {
        var property = ResolveTarget(resolver, path, rule, recorder);
        return property is not null && IsReadable(property, recorder) ? property : null;
    }

    private static bool IsReadable(RegisteredSubjectProperty property, ISubjectPathRecorder? recorder)
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
            AddAttribute(property, KnownAttributes.Configuration, recorder);
        }

        if (!isReadable && configuration?.GetValue() is not ConfigurationMetadata { IsSecret: true } &&
            property.TryGetAttribute(KnownAttributes.State) is null)
        {
            AddAttribute(property, KnownAttributes.State, recorder);
        }

        return isReadable;
    }

    private static void AddAttribute(RegisteredSubjectProperty property, string attributeName, ISubjectPathRecorder recorder) =>
        recorder.AddProperty(new PropertyReference(property.Subject, $"{property.Name}@{attributeName}"));

    /// <summary>
    /// Resolves the property or operation a path ends at: the last segment names it, the part before it leads to its subject.
    /// </summary>
    public static RegisteredSubjectProperty? ResolveTarget(SubjectPathResolver resolver, string path, IInterceptorSubject rule, ISubjectPathRecorder? recorder = null)
    {
        var lastSeparator = path.LastIndexOf(Separator);
        var name = lastSeparator < 0 ? path : path[(lastSeparator + 1)..];
        if (name.Length == 0)
        {
            return null;
        }

        var ownerPath = lastSeparator == 0 ? "/" : path[..Math.Max(lastSeparator, 0)];
        var owner = lastSeparator < 0 ? rule : ResolveSubject(resolver, ownerPath, rule, recorder);
        return owner is null ? null : FindProperty(owner, name, recorder);
    }

    private static RegisteredSubjectProperty? FindProperty(IInterceptorSubject owner, string name, ISubjectPathRecorder? recorder)
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
    public static IInterceptorSubject? ResolveSubject(SubjectPathResolver resolver, string path, IInterceptorSubject rule, ISubjectPathRecorder? recorder = null)
    {
        if (path == ".")
        {
            return rule;
        }

        try
        {
            return recorder is null
                ? resolver.ResolveSubject(path, PathStyle.Canonical, rule)
                : resolver.ResolveSubject(path, PathStyle.Canonical, rule, recorder);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Written by the author of a rule: a malformed path leads nowhere instead of failing the automation.
            recorder?.MarkUnbounded();
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
