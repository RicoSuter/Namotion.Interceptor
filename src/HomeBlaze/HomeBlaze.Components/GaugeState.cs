namespace HomeBlaze.Components;

/// <summary>
/// What a <see cref="Gauge"/> shows for a reading.
/// </summary>
/// <param name="Value">The value as a number, or null when the ring is empty.</param>
/// <param name="AriaValue">The value kept inside the range, as a meter must report it, or null when the ring is empty.</param>
/// <param name="Percent">Where the value stands in the range, from 0 to 100, or null when the ring is empty.</param>
/// <param name="Range">The range of the ring, or null when none is known.</param>
/// <param name="Error">The widget argument error shown in place of the gauge, or null.</param>
public readonly record struct GaugeState(double? Value, double? AriaValue, double? Percent, ValueRange? Range, string? Error);
