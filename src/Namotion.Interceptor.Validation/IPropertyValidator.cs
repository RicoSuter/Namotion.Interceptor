using System.ComponentModel.DataAnnotations;

namespace Namotion.Interceptor.Validation;

/// <summary>
/// Implementation of a property value validator.
/// </summary>
public interface IPropertyValidator
{
    /// <summary>
    /// Validates the new value of a property.
    /// </summary>
    /// <param name="property">The property.</param>
    /// <param name="value">The new property.</param>
    /// <returns>The validation result.</returns>
    IEnumerable<ValidationResult> Validate(PropertyReference property, object? value);
}

