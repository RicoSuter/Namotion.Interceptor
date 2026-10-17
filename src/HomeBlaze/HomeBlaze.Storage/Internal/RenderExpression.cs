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

    // No production code reads this: a render reads the value through GetText, which also reads the version of the paths,
    // so it renders again when the target of an absolute path appears. Its change notification tells other observers.
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

    private RegisteredSubjectProperty? ResolveProperty()
    {
        try
        {
            return _pathResolver.ResolveScopedDisplayableProperty(Path, PathStyle.Canonical, Parent);
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
    public string GetText(ITimeZoneDisplay? timeZone) =>
        // The property is read here and not through Value, which would resolve the path a second time.
        // The render that reads it records this read like it records one of Value.
        GetText(ResolveProperty(), Format, timeZone);

    /// <summary>
    /// Gets the value of a property as a page shows it in the format, with dates in the time zone or,
    /// when it is null, in that of the server. A missing property or value gives an empty text.
    /// </summary>
    internal static string GetText(RegisteredSubjectProperty? property, ExpressionFormat format, ITimeZoneDisplay? timeZone)
    {
        var value = property?.GetValue();
        if (property is null || value is null)
        {
            return string.Empty;
        }

        return format == ExpressionFormat.Raw
            ? RawValueFormatter.Format(value)
            : property.GetPropertyDisplayValue(value, timeZone);
    }
}
