using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Components;

/// <summary>
/// One case of a <see cref="ValueMap"/>: what a value with this text gets.
/// </summary>
[ExcludeFromCreation]
[PageLayout]
[InterceptorSubject]
public partial class ValueMapCase : IConfigurable
{
    private ParsedColor? _parsedColor;

    /// <summary>
    /// The value this case is for, as its raw text, for example "True" or the name of an enum value.
    /// </summary>
    [Configuration]
    public partial string? Value { get; set; }

    /// <summary>
    /// The text shown for the value. Without it the formatted value is shown.
    /// </summary>
    [Configuration]
    public partial string? Text { get; set; }

    /// <summary>
    /// The color of the value: the name of a theme color, such as "Success", or a CSS color, such as "#ffffff80".
    /// See <see cref="ValueColor.TryParse"/> for what is taken.
    /// </summary>
    [Configuration]
    public partial string? Color { get; set; }

    /// <summary>
    /// Name of the icon shown for the value, for example "Lightbulb".
    /// </summary>
    [Configuration]
    public partial string? Icon { get; set; }

    /// <summary>
    /// Gets the parsed <see cref="Color"/>, or null when it is no color. The result is kept until the text changes,
    /// so that a render parses nothing and gets the same instance.
    /// </summary>
    public ValueColor? GetColor()
    {
        var text = Color;
        var parsed = _parsedColor;
        if (parsed is null || !string.Equals(parsed.Text, text, StringComparison.Ordinal))
        {
            // Replaced as a whole, so that a render on another thread sees the text and its color together.
            parsed = new ParsedColor(text, ValueColor.TryParse(text, out var color) ? color : null);
            _parsedColor = parsed;
        }

        return parsed.Color;
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    private sealed record ParsedColor(string? Text, ValueColor? Color);
}
