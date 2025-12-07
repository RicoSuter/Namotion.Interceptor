using System.Collections;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Core.Pages;

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
            var property = registered?.TryGetProperty(segment);

            object? value;
            bool isSubjectReference;

            // Try registry first, fall back to reflection
            if (property != null)
            {
                if (!property.HasChildSubjects)
                    return null;

                value = property.GetValue();
                isSubjectReference = property.IsSubjectReference;
            }
            else
            {
                // Fall back to reflection for unregistered subjects
                var propInfo = current.GetType().GetProperty(segment);
                if (propInfo == null)
                    return null;

                value = propInfo.GetValue(current);
                if (value == null)
                    return null;

                isSubjectReference = value is IInterceptorSubject;
                var isCollection = !isSubjectReference && (value is IDictionary || (value is IEnumerable && value is not string));

                if (!isSubjectReference && !isCollection)
                    return null;
            }

            if (value == null)
                return null;

            // Direct subject reference
            if (isSubjectReference)
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
