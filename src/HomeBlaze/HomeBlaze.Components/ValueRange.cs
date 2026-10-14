using System.Diagnostics.CodeAnalysis;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Services;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Components;

/// <summary>
/// The range of a number from <see cref="Minimum"/> to <see cref="Maximum"/>.
/// </summary>
public readonly record struct ValueRange(double Minimum, double Maximum);

/// <summary>
/// Finds the range of a number that a widget shows or sets.
/// </summary>
public static class ValueRangeResolver
{
    /// <summary>
    /// The error of a widget whose number has no range.
    /// </summary>
    public const string MissingRangeError = "Needs a range: set Minimum and Maximum";

    /// <summary>
    /// Resolves each end of the range from the widget argument, otherwise from the numeric
    /// <see cref="KnownAttributes.Minimum"/> or <see cref="KnownAttributes.Maximum"/> attribute of the property,
    /// otherwise from 0 to 1 for a property with the unit <see cref="StateUnit.Percent"/>.
    /// </summary>
    /// <param name="property">The property of the number, or null when the path leads nowhere.</param>
    /// <param name="minimum">The <c>Minimum</c> argument of the widget.</param>
    /// <param name="maximum">The <c>Maximum</c> argument of the widget.</param>
    /// <param name="range">The range, when one was found.</param>
    /// <param name="error"><see cref="MissingRangeError"/> when an end of the range was not found, otherwise null.</param>
    public static bool TryResolve(RegisteredSubjectProperty? property, double? minimum, double? maximum,
        out ValueRange range, [NotNullWhen(false)] out string? error)
    {
        var isPercent = property?.GetStateMetadata()?.Unit == StateUnit.Percent;
        var resolvedMinimum = minimum ?? ReadNumberAttribute(property, KnownAttributes.Minimum) ?? (isPercent ? 0 : null);
        var resolvedMaximum = maximum ?? ReadNumberAttribute(property, KnownAttributes.Maximum) ?? (isPercent ? 1 : null);
        if (resolvedMinimum is null || resolvedMaximum is null)
        {
            range = default;
            error = MissingRangeError;
            return false;
        }

        range = new ValueRange(resolvedMinimum.Value, resolvedMaximum.Value);
        error = null;
        return true;
    }

    // An attribute that external sources provide can hold anything, and a value that is no number is ignored.
    private static double? ReadNumberAttribute(RegisteredSubjectProperty? property, string attributeName) =>
        property?.TryGetAttribute(attributeName)?.GetValue() switch
        {
            double number when double.IsFinite(number) => number,
            float number when float.IsFinite(number) => number,
            decimal number => (double)number,
            int number => number,
            long number => number,
            short number => number,
            byte number => number,
            sbyte number => number,
            uint number => number,
            ulong number => number,
            ushort number => number,
            _ => null
        };
}
