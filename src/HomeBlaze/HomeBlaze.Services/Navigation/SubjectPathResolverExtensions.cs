using System.Text.RegularExpressions;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;

namespace HomeBlaze.Services.Navigation;

public static partial class SubjectPathResolverExtensions
{
    /// <summary>
    /// Registers the SubjectPathResolver service which provides path resolution and building capabilities.
    /// Requires WithRegistry() and WithLifecycle() to be called first.
    /// </summary>
    public static IInterceptorSubjectContext WithPathResolver(this IInterceptorSubjectContext context)
    {
        return context
            .WithLifecycle()
            .WithService(() => new SubjectPathResolver());
    }

    /// <summary>
    /// Resolves a property value from a bracket-notation path.
    /// Path format: "Children[key].Property" or "Property[index].SubProperty"
    /// </summary>
    public static object? ResolveValue(this SubjectPathResolver resolver, IInterceptorSubject root, string path)
    {
        // Split "Children[key].Property" into "Children[key]" and "Property"
        var lastDot = path.LastIndexOf('.');
        if (lastDot < 0) return null;

        var subjectPath = path.Substring(0, lastDot);
        var propertyName = path.Substring(lastDot + 1);

        var subject = resolver.ResolveSubject(root, ConvertBracketPath(subjectPath));
        if (subject == null) return null;

        // Try registry first for tracked property access
        var registry = root.Context.TryGetService<ISubjectRegistry>();
        var registered = registry?.TryGetRegisteredSubject(subject);
        var property = registered?.TryGetProperty(propertyName);

        if (property != null)
            return property.GetValue();

        // Fallback to reflection
        return subject.GetType().GetProperty(propertyName)?.GetValue(subject);
    }

    /// <summary>
    /// Converts bracket notation to slash notation for ResolveSubject.
    /// "Children[key].Prop" becomes "Children/key/Prop"
    /// </summary>
    private static string ConvertBracketPath(string path)
    {
        return BracketRegex().Replace(path, "/$1").Replace(".", "/");
    }

    [GeneratedRegex(@"\[([^\]]+)\]")]
    private static partial Regex BracketRegex();
}
