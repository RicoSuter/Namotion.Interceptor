using System.Diagnostics.CodeAnalysis;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Components.Inputs;
using HomeBlaze.Services;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Components;

/// <summary>
/// The range of a number from <see cref="Minimum"/> to <see cref="Maximum"/>.
/// </summary>
internal readonly record struct ValueRange(double Minimum, double Maximum);

/// <summary>
/// Finds the range of a number that a widget shows or sets.
/// </summary>
internal static class ValueRangeResolver
{
    /// <summary>
    /// The error of a widget whose number has no range.
    /// </summary>
    public const string MissingRangeError = "Needs a range: set Minimum and Maximum";

    /// <summary>
    /// Resolves the range of a number, see <see cref="TryFindEnds"/>, and checks that both ends are finite and that
    /// the maximum is above the minimum.
    /// </summary>
    /// <param name="property">The property of the number, or null when the path leads nowhere.</param>
    /// <param name="minimum">The <c>Minimum</c> argument of the widget.</param>
    /// <param name="maximum">The <c>Maximum</c> argument of the widget.</param>
    /// <param name="range">The range when it is valid, otherwise default.</param>
    /// <param name="error">
    /// <see cref="MissingRangeError"/> when an end was not found, the widget argument error of an empty range, otherwise null.
    /// </param>
    public static bool TryResolve(RegisteredSubjectProperty? property, double? minimum, double? maximum,
        out ValueRange range, [NotNullWhen(false)] out string? error)
    {
        if (!TryFindEnds(property, minimum, maximum, out var ends))
        {
            range = default;
            error = MissingRangeError;
            return false;
        }

        if (!double.IsFinite(ends.Minimum) || !double.IsFinite(ends.Maximum) || ends.Maximum <= ends.Minimum)
        {
            range = default;
            error = $"Maximum must be above Minimum: {NumberValues.Format(ends.Minimum)} to {NumberValues.Format(ends.Maximum)}";
            return false;
        }

        range = ends;
        error = null;
        return true;
    }

    /// <summary>
    /// Finds each end of the range from the widget argument, otherwise from the numeric
    /// <see cref="KnownAttributes.Minimum"/> or <see cref="KnownAttributes.Maximum"/> attribute of the property,
    /// otherwise from 0 to 1 for a property with the unit <see cref="StateUnit.Percent"/>. Does not check the range.
    /// </summary>
    /// <param name="property">The property of the number, or null when the path leads nowhere.</param>
    /// <param name="minimum">The <c>Minimum</c> argument of the widget.</param>
    /// <param name="maximum">The <c>Maximum</c> argument of the widget.</param>
    /// <param name="ends">The ends, also of an empty range, when both were found.</param>
    public static bool TryFindEnds(RegisteredSubjectProperty? property, double? minimum, double? maximum, out ValueRange ends)
    {
        var resolvedMinimum = minimum ?? ReadNumberAttribute(property, KnownAttributes.Minimum) ?? (IsPercent(property) ? 0 : null);
        var resolvedMaximum = maximum ?? ReadNumberAttribute(property, KnownAttributes.Maximum) ?? (IsPercent(property) ? 1 : null);
        if (resolvedMinimum is null || resolvedMaximum is null)
        {
            ends = default;
            return false;
        }

        ends = new ValueRange(resolvedMinimum.Value, resolvedMaximum.Value);
        return true;
    }

    /// <summary>
    /// Gets where a value stands in the range of a number widget, the range by <see cref="TryResolve"/>. Only a number
    /// needs a range: a path that leads nowhere, which every value widget shows as missing, and a property that holds no
    /// number give no range and no error when no range is found. An empty range is an error also for them.
    /// </summary>
    /// <param name="property">The property of the value, or null when the path leads nowhere.</param>
    /// <param name="value">The value of the property, read once by the caller.</param>
    /// <param name="minimum">The <c>Minimum</c> argument of the widget.</param>
    /// <param name="maximum">The <c>Maximum</c> argument of the widget.</param>
    public static ValueRangeState GetState(RegisteredSubjectProperty? property, object? value, double? minimum, double? maximum)
    {
        if (!TryResolve(property, minimum, maximum, out var range, out var error))
        {
            var isNumber = property is not null && SubjectPathSelection.MatchesKind(property, SubjectPathKinds.Number);
            return !isNumber && error == MissingRangeError
                ? default
                : new ValueRangeState(null, null, null, error);
        }

        var percent = ValueReader.GetProgressPercent(value, range.Minimum, range.Maximum);
        if (percent is null || !NumberValues.TryGetNumber(value, out var number))
        {
            return new ValueRangeState(null, null, range, null);
        }

        return new ValueRangeState(Math.Clamp(number, range.Minimum, range.Maximum), percent, range, null);
    }

    private static bool IsPercent(RegisteredSubjectProperty? property) =>
        property?.GetStateMetadata()?.Unit == StateUnit.Percent;

    // An attribute that external sources provide can hold anything, and a value that is no finite number is ignored.
    private static double? ReadNumberAttribute(RegisteredSubjectProperty? property, string attributeName) =>
        NumberValues.TryGetNumber(property?.TryGetAttribute(attributeName)?.GetValue(), out var number) && double.IsFinite(number)
            ? number
            : null;
}
