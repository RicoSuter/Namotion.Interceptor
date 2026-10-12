using MudBlazor;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Components;

/// <summary>
/// What a value widget shows for the value its path leads to.
/// </summary>
/// <param name="Property">The property the path ends at, or null when the path leads nowhere.</param>
/// <param name="Value">The value of the property, or null when it is missing.</param>
/// <param name="Text">The text to show. For a missing value it is <see cref="ValueReader.MissingText"/>.</param>
/// <param name="Color">The colour of the matched case, or <see cref="Color.Default"/>.</param>
/// <param name="Icon">The icon name of the matched case, or null.</param>
/// <param name="MapError">What is wrong with the map the widget refers to, or null.</param>
public sealed record ValueReading(
    RegisteredSubjectProperty? Property,
    object? Value,
    string Text,
    Color Color,
    string? Icon,
    string? MapError)
{
    /// <summary>Whether there is no value to show.</summary>
    public bool IsMissing => Value is null;
}
