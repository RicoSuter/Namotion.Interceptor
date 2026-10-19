namespace HomeBlaze.Components;

/// <summary>
/// Where the number of a <see cref="Gauge"/> or a <see cref="Progress"/> stands in its range.
/// </summary>
/// <param name="AriaValue">The value kept inside the range, as a meter must report it, or null when there is no number to show.</param>
/// <param name="Percent">Where the value stands in the range, from 0 to 100, or null when there is no number to show.</param>
/// <param name="Range">The range, or null when none is known.</param>
/// <param name="Error">The widget argument error shown in place of the widget, or null.</param>
internal readonly record struct ValueRangeState(double? AriaValue, double? Percent, ValueRange? Range, string? Error);
