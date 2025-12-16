using System.Collections;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;

namespace HomeBlaze.Services;

public static class SubjectPathResolverExtensions
{
    /// <summary>
    /// Registers the SubjectPathResolver service which provides path resolution and building capabilities.
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
    /// Resolves a subject from a relative path expression.
    /// </summary>
    /// <remarks>
    /// Supported path prefixes:
    /// <list type="bullet">
    ///   <item><c>Root.</c> - Absolute path from root subject (e.g., "Root.Children[demo]")</item>
    ///   <item><c>this.</c> - Path relative to currentSubject (e.g., "this.Root.Children[demo]")</item>
    ///   <item><c>../</c> - Navigate up to parent (e.g., "../Sibling.Property")</item>
    ///   <item>No prefix - Path relative to currentSubject</item>
    /// </list>
    /// Returns null if path is ambiguous (multiple parents) or invalid.
    /// </remarks>
    /// <param name="resolver">The path resolver.</param>
    /// <param name="path">Path with optional prefix.</param>
    /// <param name="currentSubject">Subject for relative paths (required for non-Root. paths).</param>
    /// <returns>Resolved subject, or null if path invalid or ambiguous.</returns>
    public static IInterceptorSubject? ResolveFromRelativePath(
        this SubjectPathResolver resolver,
        string path,
        IInterceptorSubject? currentSubject = null)
    {
        if (string.IsNullOrEmpty(path))
            return currentSubject;

        // Handle "Root" or "Root." prefix - absolute path from root
        // Let the resolver handle the prefix stripping internally
        if (path == "Root" || path.StartsWith("Root."))
        {
            return resolver.ResolveSubject(path);
        }

        // Handle "this." prefix - relative to currentSubject
        if (path.StartsWith("this."))
        {
            if (currentSubject == null)
                return null;
            return resolver.ResolveSubject(path["this.".Length..], root: currentSubject);
        }

        // Need current subject for relative paths
        if (currentSubject == null)
            return null;

        // Handle "../" segments - navigate up parent chain
        while (path.StartsWith("../"))
        {
            path = path[3..];
            var registered = currentSubject.TryGetRegisteredSubject();
            if (registered == null)
                return null;

            var parents = registered.Parents;
            if (parents.Length == 0)
                return null;
            if (parents.Length > 1)
                return null;  // Ambiguous - multiple parents

            currentSubject = parents[0].Property.Subject;
        }

        return string.IsNullOrEmpty(path)
            ? currentSubject
            : resolver.ResolveSubject(path, root: currentSubject);
    }

    /// <summary>
    /// Resolves a property value from a relative path expression.
    /// Supports Root. prefix and inline subject lookup for RenderExpression compatibility.
    /// </summary>
    /// <param name="resolver">The path resolver.</param>
    /// <param name="path">Path to resolve (e.g., "Root.Children[demo].Temperature" or "motor.Speed").</param>
    /// <param name="currentSubject">Current subject for relative paths.</param>
    /// <param name="inlineSubjects">Dictionary of inline subjects (for "key.Property" syntax).</param>
    /// <returns>Resolved property value, or null if not found.</returns>
    public static object? ResolveValueFromRelativePath(
        this SubjectPathResolver resolver,
        string path,
        IInterceptorSubject? currentSubject,
        IDictionary<string, IInterceptorSubject>? inlineSubjects = null)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        // Handle "Root" or "Root." prefix - let resolver handle it
        if (path == "Root" || path.StartsWith("Root."))
        {
            return resolver.ResolveValue(path);
        }

        // Handle inline subject lookup (e.g., "conveyor.Temperature")
        if (inlineSubjects != null)
        {
            var dotIndex = path.IndexOf('.');
            if (dotIndex > 0)
            {
                var key = path[..dotIndex];
                if (inlineSubjects.TryGetValue(key, out var child))
                {
                    return resolver.ResolveValue(path[(dotIndex + 1)..], child);
                }
            }
        }

        // Standard relative path
        return resolver.ResolveValue(path, currentSubject);
    }

    /// <summary>
    /// Resolves a property value from a bracket-notation path.
    /// Path format: "Children[key].Property" or "Children[key].Children[key2].Property"
    /// </summary>
    /// <param name="resolver">The path resolver.</param>
    /// <param name="path">Bracket-notation path to the property.</param>
    /// <param name="root">Root subject to resolve from.</param>
    /// <returns>Resolved property value, or null if not found.</returns>
    public static object? ResolveValue(
        this SubjectPathResolver resolver,
        string path,
        IInterceptorSubject? root = null)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        // Find the last property separator (dot not inside brackets)
        var lastDotIndex = FindLastPropertySeparator(path);
        if (lastDotIndex < 0)
        {
            // No dot found - path is just a property name on root
            var subject = root ?? resolver.ResolveSubject(string.Empty);
            return subject?.TryGetRegisteredProperty(path)?.GetValue();
        }

        var subjectPath = path[..lastDotIndex];
        var propertyName = path[(lastDotIndex + 1)..];

        var resolvedSubject = resolver.ResolveSubject(subjectPath, PathFormat.Bracket, root);
        return resolvedSubject?.TryGetRegisteredProperty(propertyName)?.GetValue();
    }

    /// <summary>
    /// Finds a child subject by index in a collection/dictionary property.
    /// Used by SubjectBrowser and other navigation components.
    /// </summary>
    /// <param name="property">The property containing the collection/dictionary.</param>
    /// <param name="indexStr">The key (for dictionaries) or index (for collections) as a string.</param>
    /// <returns>The child subject, or null if not found.</returns>
    public static IInterceptorSubject? FindChildByIndex(RegisteredSubjectProperty property, string indexStr)
    {
        var value = property.GetValue();
        if (value == null)
            return null;

        // Handle Dictionary<string, IInterceptorSubject>
        if (value is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                var key = entry.Key?.ToString();
                if (key == indexStr && entry.Value is IInterceptorSubject subject)
                    return subject;
            }
        }
        // Handle List/Array with numeric index
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

    /// <summary>
    /// Finds the last dot that separates subject path from property name.
    /// Dots inside brackets are not separators.
    /// </summary>
    private static int FindLastPropertySeparator(string path)
    {
        var bracketDepth = 0;
        var lastDotIndex = -1;

        for (var i = 0; i < path.Length; i++)
        {
            var c = path[i];
            switch (c)
            {
                case '[':
                    bracketDepth++;
                    break;
                case ']':
                    bracketDepth--;
                    break;
                case '.' when bracketDepth == 0:
                    lastDotIndex = i;
                    break;
            }
        }

        return lastDotIndex;
    }
}
