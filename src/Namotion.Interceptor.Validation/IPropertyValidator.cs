using System.ComponentModel.DataAnnotations;

namespace Namotion.Interceptor.Validation;

/// <summary>
/// Implementation of a property value validator.
/// </summary>
public interface IPropertyValidator
{
    /// <summary>
    /// Validates the new value of a property. The context is passed by <c>in</c>, so
    /// implementations must return a collection rather than use <c>yield</c> (CS1623).
    /// </summary>
    /// <typeparam name="TProperty">The property type.</typeparam>
    /// <param name="context">The validation context carrying the property, value, and attempted origin.</param>
    /// <returns>The validation results, empty if valid.</returns>
    /// <remarks>
    /// <typeparamref name="TProperty"/> is the property type for writes through the generated property setters, and
    /// <c>object</c> for untyped writes through <c>SetPropertyValueWithInterception</c>, which include derived property
    /// recalculations and dynamic properties. In the transaction commit checks it can also be <c>object</c> for a typed
    /// write, or the property type for an untyped one, depending on which writes ran earlier in the process. Test the
    /// value (<c>context.Value is int speed</c>), not <c>typeof(TProperty)</c>.
    /// </remarks>
    IEnumerable<ValidationResult> Validate<TProperty>(in PropertyValidationContext<TProperty> context);
}

