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
    IEnumerable<ValidationResult> Validate<TProperty>(in PropertyValidationContext<TProperty> context);
}

