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
    /// Path format: "Children[key].Property" or "Root.Property[index].SubProperty"
    /// </summary>
    public static object? ResolveValue(this SubjectPathResolver resolver, IInterceptorSubject root, string path)
    {
        // TODO: Implement alloc free (span?) and make simpler (hacky)
        var segments = path.Split(['.', '/', '[', ']'], StringSplitOptions.RemoveEmptyEntries)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToArray();

        var variable = segments.FirstOrDefault();
        if (variable != null)
        {
            var propertyName = segments.Last();
            var subjectPath = string.Join('/', segments.Skip(1).Take(segments.Length - 2).ToArray());

            var x = resolver.ResolveSubject(root, subjectPath);
            if (x == null) return null;
           
            root = x;

            var subject = resolver.ResolveSubject(root, ConvertBracketPath(subjectPath));
            if (subject == null) return null;

            var property = subject.TryGetRegisteredProperty(propertyName);
            if (property != null)
                return property.GetValue();
        }

        return null;
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
