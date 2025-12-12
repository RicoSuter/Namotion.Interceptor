using System.Text.RegularExpressions;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
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
    /// Path format: "Children[key].Property" or "Children[key].Children[key2].Property"
    /// </summary>
    public static object? ResolveValue(this SubjectPathResolver resolver, IInterceptorSubject root, string path)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        // Convert bracket notation to slash notation: Children[demo].Children[conveyor].Temperature
        // becomes: Children/demo/Children/conveyor/Temperature
        var slashPath = ConvertBracketPath(path);
        var segments = slashPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 0)
            return null;

        // Last segment is the property name to read
        var propertyName = segments[^1];

        // Everything before the last segment is the subject path
        var subjectPath = segments.Length > 1
            ? string.Join('/', segments[..^1])
            : string.Empty;

        var subject = string.IsNullOrEmpty(subjectPath)
            ? root
            : resolver.ResolveSubject(root, subjectPath);

        if (subject == null)
            return null;

        var property = subject.TryGetRegisteredProperty(propertyName);
        return property?.GetValue();
    }

    /// <summary>
    /// Converts bracket notation to slash notation for ResolveSubject.
    /// "Children[key.json].Prop" becomes "Children/key.json/Prop"
    /// Preserves dots inside bracket keys (e.g., file extensions).
    /// </summary>
    private static string ConvertBracketPath(string path)
    {
        // Replace "]." with "/" (end of key followed by property accessor)
        // Replace "[" with "/" (start of key)
        // Remove remaining "]" at end of path
        return path
            .Replace("].", "/")
            .Replace("[", "/")
            .Replace("]", "");
    }
}
