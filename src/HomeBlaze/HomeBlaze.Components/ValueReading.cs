using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Components;

/// <summary>
/// What a value widget shows for the value its path leads to.
/// </summary>
/// <param name="Property">The property the path ends at, or null when the path leads nowhere.</param>
/// <param name="Value">The value of the property, or null when it is missing.</param>
/// <param name="Text">The text to show. For a missing value it is <see cref="ValueReader.MissingText"/>.</param>
/// <param name="Color">The color of the matched case, or <see cref="ValueColor.Default"/>.</param>
/// <param name="Icon">The icon name of the matched case, or null.</param>
/// <param name="MapError">What is wrong with the map the widget refers to, or null.</param>
public sealed record ValueReading(
    RegisteredSubjectProperty? Property,
    object? Value,
    string Text,
    ValueColor Color,
    string? Icon,
    string? MapError)
{
    /// <summary>The reading of a widget that has no value to show and knows no property and no map.</summary>
    public static ValueReading Missing { get; } = new(null, null, ValueReader.MissingText, ValueColor.Default, null, null);

    /// <summary>
    /// The text color of the matched case on a tile or a chip that <see cref="Color"/> fills, or
    /// <see cref="ValueColor.Default"/> for the text color that goes with the fill.
    /// </summary>
    public ValueColor TextColor { get; init; } = ValueColor.Default;

    /// <summary>Whether a case of the map matched, which can give a text and a color also when there is no value.</summary>
    public bool HasCase { get; init; }

    /// <summary>Whether there is no value to show and no case of the map matched.</summary>
    public bool IsMissing => Value is null && !HasCase;
}
