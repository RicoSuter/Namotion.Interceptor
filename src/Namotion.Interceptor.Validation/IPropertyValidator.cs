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
    /// <typeparamref name="TProperty"/> is not always the property type. In the transaction handler checks (the commit
    /// check and the pre-apply check), it is the property type for <c>int</c>, <c>long</c>, <c>double</c>,
    /// <c>float</c>, <c>decimal</c>, <c>bool</c>, <c>DateTime</c>, <c>DateTimeOffset</c>, <c>TimeSpan</c> and
    /// <c>Guid</c>, and <c>object</c> for every other type, including their nullable forms. It is also <c>object</c>
    /// for writes through <c>SetPropertyValueWithInterception</c>, which include derived property recalculations.
    /// Rely on neither: test the value (<c>context.Value is int speed</c>) instead of <c>typeof(TProperty)</c>.
    /// </remarks>
    IEnumerable<ValidationResult> Validate<TProperty>(in PropertyValidationContext<TProperty> context);
}

