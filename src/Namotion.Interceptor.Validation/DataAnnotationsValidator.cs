using System.ComponentModel.DataAnnotations;
using System.Collections.Concurrent;

namespace Namotion.Interceptor.Validation;

public class DataAnnotationsValidator : IPropertyValidator
{
    private static readonly ConcurrentDictionary<(Type SubjectType, string PropertyName), bool> RequiresValidationCache = new();

    public IEnumerable<ValidationResult> Validate(PropertyReference property, object? value)
    {
        if (CheckRequiresValidation(property))
        {
            var validationContext = new ValidationContext(property.Subject)
            {
                MemberName = property.Name
            };

            var results = new List<ValidationResult>(capacity: 2);
            Validator.TryValidateProperty(value, validationContext, results);
            return results;
        }

        return [];
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
