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
    /// <typeparam name="TProperty">The property type.</typeparam>
    /// <param name="property">The property.</param>
    /// <param name="value">The new value.</param>
    /// <returns>The validation results, empty if valid.</returns>
    IEnumerable<ValidationResult> Validate<TProperty>(PropertyReference property, TProperty value);
}

