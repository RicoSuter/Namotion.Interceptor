using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Components.Inputs;
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
    /// <param name="minimum">The <c>Minimum</c> argument of the control, which can give a number its range.</param>
    /// <param name="maximum">The <c>Maximum</c> argument of the control, which can give a number its range.</param>
    public static ControlKind Resolve(RegisteredSubjectProperty? property, double? minimum, double? maximum)
    {
        if (property is null)
        {
            return ControlKind.Value;
        }

        if (SubjectPathSelection.MatchesKind(property, SubjectPathKinds.Operation))
        {
            return ControlKind.OperationButton;
        }

        // The property holds no operation here, so a null current value decides the same as PropertyWriter.IsWritable
        // would with the real value, without a tracked read of a value that the chosen view reads anyway.
        var isWritable = PropertyWriter.IsWritable(property, currentValue: null);
        if (SubjectPathSelection.MatchesKind(property, SubjectPathKinds.Boolean))
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

        if (SubjectPathSelection.MatchesKind(property, SubjectPathKinds.Number) &&
            ValueRangeResolver.TryResolve(property, minimum, maximum, out _, out _))
        {
            return ControlKind.Slider;
        }

        return ControlKind.Value;
    }
}
