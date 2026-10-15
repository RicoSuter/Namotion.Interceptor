using System.Buffers;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using HomeBlaze.Abstractions;
using HomeBlaze.Services;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Attributes;

namespace HomeBlaze.Components;

/// <summary>
/// Finds the rows of a <see cref="SubjectTable"/>, the headers of its columns and what its rows link to.
/// </summary>
public static class SubjectTableRows
{
    /// <summary>The widget argument error of a table without a source.</summary>
    public const string MissingSourceError = "Needs a Source: a folder or a property that holds subjects";

    private const string BrowserRoute = "/browser";

    // The unreserved characters of a URI and the separator of the segments.
    private static readonly SearchValues<char> RouteCharacters =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~/");

    // Keyed weakly, so that a type of a plugin that is unloaded is not kept alive.
    private static readonly ConditionalWeakTable<Type, HashSet<string>> TypeNames = new();

    /// <summary>
    /// Finds the rows of a table. A source that leads to a property that holds subjects gives the subjects of the
    /// property; a source that leads to a subject gives the children of its <see cref="InlinePathsAttribute"/> property.
    /// </summary>
    /// <param name="resolver">The resolver of the source.</param>
    /// <param name="widget">The subject the source is relative to, usually the table.</param>
    /// <param name="source">The path of the source.</param>
    /// <param name="typeName">The full name of a class, base class or interface that a row must have, or null or empty for every row.</param>
    /// <param name="rows">The rows in the order of the source, empty on an error.</param>
    /// <param name="error">The widget argument error, or null.</param>
    /// <returns>Whether the source leads to subjects. A source with no subject of the type gives true and no rows.</returns>
    public static bool TryGetRows(
        SubjectPathResolver resolver,
        IInterceptorSubject widget,
        string? source,
        string? typeName,
        out ImmutableArray<SubjectPropertyChild> rows,
        out string? error)
    {
        rows = ImmutableArray<SubjectPropertyChild>.Empty;
        if (string.IsNullOrEmpty(source))
        {
            error = MissingSourceError;
            return false;
        }

        var property = FindRowsProperty(resolver, widget, source);
        if (property is null)
        {
            error = $"Cannot list subjects of: {source}";
            return false;
        }

        // The children are kept by the registry and are not tracked. This read is, so that the table renders
        // again when the property gets other subjects. Do not remove it, although its result is not used.
        _ = property.GetValue();

        error = null;
        rows = Filter(property.Children, typeName);
        return true;
    }

    /// <summary>
    /// Whether the type, one of its base classes or one of its interfaces has the full name, with regard to case.
    /// </summary>
    public static bool MatchesType(Type type, string typeName) =>
        TypeNames.GetValue(type, static type => CollectTypeNames(type)).Contains(typeName);

    /// <summary>
    /// Gets the name of a row: the title of the subject, otherwise its key or index, otherwise the name of its type.
    /// </summary>
    public static string GetTitle(SubjectPropertyChild row)
    {
        if (row.Subject is ITitleProvider { Title: { Length: > 0 } title })
        {
            return title;
        }

        var key = row.Index?.ToString();
        return string.IsNullOrEmpty(key) ? row.Subject.GetType().Name : key;
    }

    /// <summary>
    /// Gets the header of a column: its label, otherwise the title of the property on the first row that has it,
    /// otherwise the property name.
    /// </summary>
    public static string GetHeader(SubjectTableColumn column, ImmutableArray<SubjectPropertyChild> rows)
    {
        var label = column.Label;
        if (!string.IsNullOrEmpty(label))
        {
            return label;
        }

        var propertyName = column.Property;
        if (string.IsNullOrEmpty(propertyName))
        {
            return string.Empty;
        }

        foreach (var row in rows)
        {
            var property = row.Subject.TryGetRegisteredProperty(propertyName);
            if (property is not null)
            {
                return property.GetDisplayName();
            }
        }

        return propertyName;
    }

    /// <summary>
    /// Gets the address of the subject in the subject browser, or null when the subject is not in the graph.
    /// </summary>
    public static string? GetLink(SubjectPathResolver resolver, IInterceptorSubject subject)
    {
        var path = resolver.GetPath(subject, PathStyle.Route);
        if (path is null)
        {
            return null;
        }

        // The path is read on every render of every row, so a path without a character to escape is used as it is.
        if (path.Length > 1 && path[0] == '/' && !path.AsSpan(1).ContainsAnyExcept(RouteCharacters))
        {
            return BrowserRoute + path;
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return BrowserRoute;
        }

        for (var index = 0; index < segments.Length; index++)
        {
            segments[index] = Uri.EscapeDataString(segments[index]);
        }

        return BrowserRoute + "/" + string.Join('/', segments);
    }

    private static RegisteredSubjectProperty? FindRowsProperty(SubjectPathResolver resolver, IInterceptorSubject widget, string source)
    {
        var property = ValueReader.ResolveProperty(resolver, widget, source);
        if (property is { CanContainSubjects: true })
        {
            return property;
        }

        var subject = ValueReader.ResolveSubject(resolver, widget, source);
        if (subject is null)
        {
            return null;
        }

        var propertyName = InlinePathsAttribute.GetInlinePathsPropertyName(subject.GetType());
        return propertyName is null ? null : subject.TryGetRegisteredProperty(propertyName);
    }

    private static ImmutableArray<SubjectPropertyChild> Filter(ImmutableArray<SubjectPropertyChild> children, string? typeName)
    {
        if (string.IsNullOrEmpty(typeName))
        {
            return children;
        }

        // The builder is only created at the first row that does not match, so a source whose rows all match costs no copy.
        ImmutableArray<SubjectPropertyChild>.Builder? builder = null;
        for (var index = 0; index < children.Length; index++)
        {
            var child = children[index];
            if (MatchesType(child.Subject.GetType(), typeName))
            {
                builder?.Add(child);
            }
            else if (builder is null)
            {
                builder = ImmutableArray.CreateBuilder<SubjectPropertyChild>(children.Length - 1);
                for (var previous = 0; previous < index; previous++)
                {
                    builder.Add(children[previous]);
                }
            }
        }

        return builder is null ? children : builder.ToImmutable();
    }

    private static HashSet<string> CollectTypeNames(Type type)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.FullName is { } name)
            {
                names.Add(name);
            }
        }

        foreach (var implemented in type.GetInterfaces())
        {
            if (implemented.FullName is { } name)
            {
                names.Add(name);
            }
        }

        return names;
    }
}
