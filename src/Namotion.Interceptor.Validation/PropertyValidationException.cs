using System.ComponentModel.DataAnnotations;
using System.Text;

namespace Namotion.Interceptor.Validation;

/// <summary>
/// Thrown when a property value is invalid. <see cref="ValidationResults"/> holds every result the validators returned
/// for the value, and the message joins their messages, one per line.
/// </summary>
public sealed class PropertyValidationException : ValidationException
{
    /// <summary>
    /// Initializes a new instance for an invalid value of <paramref name="property"/>.
    /// </summary>
    /// <param name="property">The validated property.</param>
    /// <param name="value">The invalid value.</param>
    /// <param name="validationResults">The results the validators returned, in validator order.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="validationResults"/> is empty.</exception>
    public PropertyValidationException(
        PropertyReference property,
        object? value,
        IReadOnlyList<ValidationResult> validationResults)
        : base(CreateMessage(validationResults), validatingAttribute: null, value)
    {
        Property = property;
        ValidationResults = validationResults;
    }

    /// <summary>
    /// Gets the validated property.
    /// </summary>
    public PropertyReference Property { get; }

    /// <summary>
    /// Gets the results the validators returned for the value, in validator order, each with its member names.
    /// </summary>
    public IReadOnlyList<ValidationResult> ValidationResults { get; }

    private static string CreateMessage(IReadOnlyList<ValidationResult> validationResults)
    {
        if (validationResults.Count == 0)
        {
            throw new ArgumentException("At least one validation result is required.", nameof(validationResults));
        }

        if (validationResults.Count == 1)
        {
            return validationResults[0].ErrorMessage ?? string.Empty;
        }

        var message = new StringBuilder();
        for (var index = 0; index < validationResults.Count; index++)
        {
            if (index > 0)
            {
                message.Append('\n');
            }

            message.Append(validationResults[index].ErrorMessage);
        }

        return message.ToString();
    }
}
