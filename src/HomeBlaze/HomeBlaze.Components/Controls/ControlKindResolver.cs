using HomeBlaze.Services;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Components.Controls;

/// <summary>
/// Decides which widget a control widget renders for its target.
/// </summary>
internal static class ControlKindResolver
{
    /// <summary>
    /// Gets the kind of control for a property or a method property.
    /// </summary>
    /// <param name="property">The target, or null when the path leads nowhere.</param>
    /// <param name="minimum">
    /// The <c>Minimum</c> argument of the control. Without it the lower end comes from the <c>Minimum</c> attribute of the
    /// property, then 0 for a percent, see <see cref="ValueRangeResolver.TryFindEnds"/>.
    /// </param>
    /// <param name="maximum">
    /// The <c>Maximum</c> argument of the control. Without it the upper end comes from the <c>Maximum</c> attribute of the
    /// property, then 1 for a percent, see <see cref="ValueRangeResolver.TryFindEnds"/>.
    /// </param>
    /// <param name="sliderRange">
    /// For <see cref="ControlKind.Slider"/> the ends of its range, also of an empty range, which the slider reports;
    /// otherwise default.
    /// </param>
    public static ControlKind Resolve(RegisteredSubjectProperty? property, double? minimum, double? maximum, out ValueRange sliderRange)
    {
        sliderRange = default;
        if (property is null)
        {
            return ControlKind.Value;
        }

        if (property.TryGetMethodMetadata() is not null)
        {
            return ControlKind.OperationButton;
        }

        // The property holds no operation here, so a null current value decides the same as PropertyWriter.IsWritable
        // would with the real value, without a tracked read of a value that the chosen view reads anyway.
        var isWritable = PropertyWriter.IsWritable(property, currentValue: null);
        if (property.Type == typeof(bool) || property.Type == typeof(bool?))
        {
            return isWritable ? ControlKind.Switch : ControlKind.Indicator;
        }

        if (!isWritable)
        {
            return ControlKind.Value;
        }

        if (EnumChoices.GetEnumType(property.Type) is { } enumType)
        {
            return EnumChoices.IsFlags(enumType) ? ControlKind.Value : ControlKind.Select;
        }

        if (NumberValues.IsNumberType(property.Type) &&
            ValueRangeResolver.TryFindEnds(property, minimum, maximum, out sliderRange))
        {
            return ControlKind.Slider;
        }

        return ControlKind.Value;
    }
}
