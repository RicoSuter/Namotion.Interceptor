using System.Collections;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Core.Services;

/// <summary>
/// Resolves subjects from URL-style paths using the subject registry.
/// </summary>
public static class SubjectPathResolver
{
    /// <summary>
    /// Resolves a subject from a URL-style path (e.g., "Children/Notes/Children/file.md").
    /// </summary>
    /// <param name="root">The root subject to start from.</param>
    /// <param name="registry">The subject registry for property lookups.</param>
    /// <param name="path">The path to resolve (using '/' as separator).</param>
    /// <returns>The resolved subject, or null if not found.</returns>
    public static IInterceptorSubject? ResolveSubject(
        IInterceptorSubject root,
        ISubjectRegistry registry,
        string path)
    {
        if (string.IsNullOrEmpty(path))
            return root;

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = root;

        for (int i = 0; i < segments.Length; i++)
        {
            var segment = Uri.UnescapeDataString(segments[i]);
            var registered = registry.TryGetRegisteredSubject(current);
            if (registered == null)
                return null;

            var property = registered.TryGetProperty(segment);
            if (property == null || !property.HasChildSubjects)
                return null;

            var value = property.GetValue();
            if (value == null)
                return null;

            // Direct subject reference
            if (property.IsSubjectReference)
            {
                if (value is not IInterceptorSubject subject)
                    return null;
                current = subject;
                continue;
            }

            // Collection or dictionary - next segment is key/index
            if (i + 1 >= segments.Length)
                return null;

            var indexStr = Uri.UnescapeDataString(segments[++i]);
            IInterceptorSubject? found = null;

            if (value is IDictionary dict)
            {
                // Dictionary - find by key string match
                foreach (DictionaryEntry entry in dict)
                {
                    if (entry.Key?.ToString() == indexStr && entry.Value is IInterceptorSubject s)
                    {
                        found = s;
                        break;
                    }
                }
            }
            else if (value is IEnumerable enumerable)
            {
                // Collection - find by index
                if (int.TryParse(indexStr, out var index))
                {
                    var idx = 0;
                    foreach (var item in enumerable)
                    {
                        if (idx == index && item is IInterceptorSubject s)
                        {
                            found = s;
                            break;
                        }
                        idx++;
                    }
                }
            }

            if (found == null)
                return null;

            current = found;
        }

        return current;
    }
}
