using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Components.Abstractions.TimeZones;
using HomeBlaze.Services;
using HomeBlaze.Services.Display;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Pages;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Resolves and displays a property value from a path expression.
/// A path with a / prefix resolves from the root. Every other path resolves from the page, and its
/// first segment can be the name of a block on that page.
/// </summary>
[InterceptorSubject]
[ExcludeFromBrowsing]
public partial class RenderExpression : ITitleProvider
{
    private readonly SubjectPathResolver _pathResolver;

    public string Path { get; }
    public ExpressionFormat Format { get; }

    /// <summary>What is wrong with the expression as written, or null. It is shown in place of the value.</summary>
    public string? Error { get; }

    public MarkdownFile Parent { get; }

    public string? Title => null;

    [Derived]
    public object? Value => ResolveProperty()?.GetValue();

    public RenderExpression(
        string path,
        ExpressionFormat format,
        string? error,
        MarkdownFile parent,
        SubjectPathResolver pathResolver)
    {
        Path = path;
        Format = format;
        Error = error;
        Parent = parent;
        _pathResolver = pathResolver;
    }

    /// <summary>
    /// Gets the property the path ends at, or null when the path leads nowhere.
    /// </summary>
    public RegisteredSubjectProperty? ResolveProperty()
    {
        try
        {
            return _pathResolver.ResolveScopedProperty(Path, PathStyle.Canonical, Parent);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Gets the value as the page shows it. A missing value gives an empty text.
    /// </summary>
    /// <param name="timeZone">The time zone of the viewer for dates, or null for the time zone of the server.</param>
    public string GetText(ITimeZoneDisplay? timeZone)
    {
        // Read through the derived property, which is what the tracking scope of a render records.
        var value = Value;
        if (value is null)
        {
            return string.Empty;
        }

        if (Format == ExpressionFormat.Raw)
        {
            return RawValueFormatter.Format(value);
        }

        var property = ResolveProperty();
        return property is null ? RawValueFormatter.Format(value) : property.GetPropertyDisplayValue(value, timeZone);
    }
}
