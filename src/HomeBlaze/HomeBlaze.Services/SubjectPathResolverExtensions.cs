using System.Globalization;
using HomeBlaze.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;

namespace HomeBlaze.Services;

public static class SubjectPathResolverExtensions
{
    /// <summary>
    /// Resolves a property value from a path for display. The value of a property that is not
    /// <see cref="SubjectRegistryExtensions.IsDisplayable"/>, such as a secret configuration property, is never returned.
    /// </summary>
    /// <param name="resolver">The path resolver.</param>
    /// <param name="path">Path to resolve (e.g., "/Demo/Conveyor/Temperature" or "motor/Speed").</param>
    /// <param name="style">Path style (Canonical or Route).</param>
    /// <param name="relativeTo">Base subject for relative paths.</param>
    /// <returns>Resolved property value, or null if not found or not displayable.</returns>
    public static object? ResolveValue(
        this SubjectPathResolver resolver,
        string path,
        PathStyle style,
        IInterceptorSubject? relativeTo = null)
    {
        return resolver.ResolveProperty(path, style, relativeTo).AsDisplayable()?.GetValue();
    }

    /// <summary>
    /// Resolves a property for showing its value, relative to a subject itself and not to the page that holds it: the last
    /// segment of the path is the property name, and the part before it is resolved from <paramref name="from"/>. A property
    /// that is not <see cref="SubjectRegistryExtensions.IsDisplayable"/> resolves to null.
    /// </summary>
    public static RegisteredSubjectProperty? ResolveDisplayablePropertyFrom(
        this SubjectPathResolver resolver,
        string path,
        PathStyle style,
        IInterceptorSubject from)
    {
        var lastSlash = path.LastIndexOf('/');
        var name = lastSlash < 0 ? path : path[(lastSlash + 1)..];
        if (name.Length == 0)
        {
            return null;
        }

        var owner = lastSlash < 0 ? from : resolver.ResolveSubject(lastSlash == 0 ? "/" : path[..lastSlash], style, from);
        return owner?.TryGetRegisteredProperty(name).AsDisplayable();
    }

    /// <summary>
    /// Resolves a property from a path: the part up to the last '/' resolves the subject, the rest names the property.
    /// </summary>
    /// <returns>The resolved property, or null if not found.</returns>
    internal static RegisteredSubjectProperty? ResolveProperty(
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
            return subject?.TryGetRegisteredProperty(path);
        }

        // Split: subject path up to last slash, property name after
        var subjectPath = lastSlash == 0 ? "/" : path[..lastSlash];
        var propertyName = path[(lastSlash + 1)..];

        if (string.IsNullOrEmpty(propertyName))
            return null; // Path ends with '/' - no property name

        var resolvedSubject = resolver.ResolveSubject(subjectPath, style, relativeTo);
        return resolvedSubject?.TryGetRegisteredProperty(propertyName);
    }

    /// <summary>
    /// Finds a child subject by index in a collection/dictionary property.
    /// Used by SubjectBrowser and other navigation components.
    /// </summary>
    public static IInterceptorSubject? FindChildByIndex(RegisteredSubjectProperty property, string indexStr)
    {
        var value = property.GetValue();
        return value is not null ? FindChildByIndex(property, value, indexStr) : null;
    }

    /// <summary>
    /// Finds a child subject in <paramref name="value"/>, the current value of <paramref name="property"/>. A
    /// dictionary key matches when its invariant text, see <see cref="PathExtensions.FormatPathIndex"/>, equals
    /// <paramref name="indexText"/>.
    /// </summary>
    internal static IInterceptorSubject? FindChildByIndex(RegisteredSubjectProperty property, object value, string indexText)
    {
        if (property.IsSubjectDictionary)
        {
            // Finds a string key, so that only a dictionary with other keys pays for the scan below.
            var child = SubjectLookup.FindSubjectInDictionary(value, indexText);
            if (child is not null || SubjectLookup.GetDictionaryKeyType(value.GetType()) == typeof(string))
                return child;

            // The registered children are the entries of the dictionary's last assignment, so an entry added in place
            // without assigning the dictionary again is not found.
            foreach (var entry in property.Children)
            {
                if (entry.Index is not null && KeyTextEquals(entry.Index, indexText))
                    return entry.Subject;
            }

            return null;
        }

        if (property.IsSubjectCollection && int.TryParse(indexText, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
            return SubjectLookup.FindSubjectInCollection(value, index);

        return null;
    }

    // Mirrors PathSyntax.KeyTextEquals of the registry, which is internal.
    private static bool KeyTextEquals(object key, string text)
    {
        if (key is ISpanFormattable formattable)
        {
            Span<char> buffer = stackalloc char[64];
            if (formattable.TryFormat(buffer, out var written, default, CultureInfo.InvariantCulture))
                return buffer[..written].SequenceEqual(text);
        }

        return PathExtensions.FormatPathIndex(key) == text;
    }
}
