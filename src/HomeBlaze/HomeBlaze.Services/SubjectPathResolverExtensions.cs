using System.Collections;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;

namespace HomeBlaze.Services;

public static class SubjectPathResolverExtensions
{
    /// <summary>
    /// Registers the SubjectPathResolver service.
    /// Requires WithRegistry() and WithLifecycle() to be called first.
    /// </summary>
    public static IInterceptorSubjectContext WithPathResolver(this IInterceptorSubjectContext context)
    {
        return context
            .WithLifecycle()
            .WithService(() =>
            {
                var rootManager = context.GetService<RootManager>();
                return new SubjectPathResolver(rootManager, context);
            }, _ => true);
    }

    /// <summary>
    /// Resolves a property value from a path.
    /// Splits on last '/' to separate subject path from property name,
    /// resolves the subject, then gets the property value.
    /// </summary>
    /// <param name="resolver">The path resolver.</param>
    /// <param name="path">Path to resolve (e.g., "/Demo/Conveyor/Temperature" or "motor/Speed").</param>
    /// <param name="style">Path style (Canonical or Route).</param>
    /// <param name="relativeTo">Base subject for relative paths.</param>
    /// <returns>Resolved property value, or null if not found.</returns>
    public static object? ResolveValue(
        this SubjectPathResolver resolver,
        string path,
        PathStyle style,
        IInterceptorSubject? relativeTo = null)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        var lastSlash = path.LastIndexOf('/');

        if (lastSlash < 0)
        {
            // No slash - just a property name on relativeTo or root
            var subject = relativeTo ?? resolver.ResolveSubject("/", style);
            return subject?.TryGetRegisteredProperty(path)?.GetValue();
        }

        // Split: subject path up to last slash, property name after
        var subjectPath = lastSlash == 0 ? "/" : path[..lastSlash];
        var propertyName = path[(lastSlash + 1)..];

        if (string.IsNullOrEmpty(propertyName))
            return null; // Path ends with '/' - no property name

        var resolvedSubject = resolver.ResolveSubject(subjectPath, style, relativeTo);
        return resolvedSubject?.TryGetRegisteredProperty(propertyName)?.GetValue();
    }

    /// <summary>
    /// Finds a child subject by index in a collection/dictionary property.
    /// Used by SubjectBrowser and other navigation components.
    /// </summary>
    public static IInterceptorSubject? FindChildByIndex(RegisteredSubjectProperty property, string indexStr)
    {
        var value = property.GetValue();
        if (value == null)
            return null;

        if (value is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                var key = entry.Key?.ToString();
                if (key == indexStr && entry.Value is IInterceptorSubject subject)
                    return subject;
            }
        }
        else if (value is IEnumerable enumerable and not string)
        {
            if (int.TryParse(indexStr, out var index))
            {
                var i = 0;
                foreach (var item in enumerable)
                {
                    if (i == index && item is IInterceptorSubject subject)
                        return subject;
                    i++;
                }
            }
        }

        return null;
    }
}
