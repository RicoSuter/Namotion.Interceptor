using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;

namespace Namotion.Interceptor.Validation;

public class DataAnnotationsValidator : IPropertyValidator
{
    private static readonly ConcurrentDictionary<(Type SubjectType, string PropertyName), bool> RequiresValidationCache = new();

    public IEnumerable<ValidationResult> Validate<TProperty>(PropertyReference property, TProperty value)
    {
        // Fast path: no validation attributes = no boxing, no allocation
        if (!CheckRequiresValidation(property))
        {
            return [];
        }

        // Slow path: has attributes, must box for .NET Validator API
        return ValidateCore(property, value);
    }

    private static IEnumerable<ValidationResult> ValidateCore(PropertyReference property, object? value)
    {
        var validationContext = new ValidationContext(property.Subject)
        {
            MemberName = property.Name
        };

        var results = new List<ValidationResult>(capacity: 2);
        Validator.TryValidateProperty(value, validationContext, results);
        return results;
    }

    private static bool CheckRequiresValidation(PropertyReference property)
    {
        var subjectType = property.Subject.GetType();
        var key = (subjectType, property.Name);

        return RequiresValidationCache.GetOrAdd(key, static (_, metadata) =>
        {
            if (metadata.IsDynamic)
            {
                // .NET Validator does not support dynamically added properties
                return false;
            }

            var attributes = metadata.Attributes;
            if (attributes.Count == 0)
            {
                return false;
            }

            foreach (var attribute in attributes)
            {
                if (attribute is ValidationAttribute)
                {
                    return true;
                }
            }

            return false;

        }, property.Metadata);
    }
}
