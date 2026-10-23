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
    private ParsedColor? _parsedTextColor;

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
    /// The text color on a tile or a chip that the case fills, in the same forms as <see cref="Color"/>. Without it, or
    /// with "Default", the text has the color that goes with the fill.
    /// </summary>
    [Configuration]
    public partial string? TextColor { get; set; }

    /// <summary>
    /// Gets the parsed <see cref="Color"/>, or <see cref="ValueColor.Default"/> when it is no color, which
    /// <see cref="ValueReader.ResolveMap"/> reports. The result is kept until the text changes, so that a render parses
    /// nothing and gets the same instance.
    /// </summary>
    public ValueColor GetColor() => GetParsed(Color, ref _parsedColor).Color ?? ValueColor.Default;

    /// <summary>
    /// Gets the parsed <see cref="TextColor"/>, or <see cref="ValueColor.Default"/> when it is not set or no color, kept
    /// like <see cref="GetColor"/>.
    /// </summary>
    public ValueColor GetTextColor() => GetParsed(TextColor, ref _parsedTextColor).Color ?? ValueColor.Default;

    /// <summary>Gets whether <see cref="Color"/> is a theme color, a CSS color that <see cref="ValueColor.TryParse"/> takes, or not set.</summary>
    internal bool IsColorValid() => GetParsed(Color, ref _parsedColor).Color is not null;

    /// <summary>Gets whether <see cref="TextColor"/> is a theme color, a CSS color that <see cref="ValueColor.TryParse"/> takes, or not set.</summary>
    internal bool IsTextColorValid() => GetParsed(TextColor, ref _parsedTextColor).Color is not null;

    private static ParsedColor GetParsed(string? text, ref ParsedColor? cache)
    {
        var parsed = cache;
        if (parsed is null || !string.Equals(parsed.Text, text, StringComparison.Ordinal))
        {
            // Replaced as a whole, so that a render on another thread sees the text and its color together.
            parsed = new ParsedColor(text, ValueColor.TryParse(text, out var color) ? color : null);
            cache = parsed;
        }

        return parsed;
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    private sealed record ParsedColor(string? Text, ValueColor? Color);
}
