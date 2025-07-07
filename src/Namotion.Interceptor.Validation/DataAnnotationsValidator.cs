using System.ComponentModel.DataAnnotations;

namespace Namotion.Interceptor.Validation;

public class DataAnnotationsValidator : IPropertyValidator
{
    public IEnumerable<ValidationResult> Validate(PropertyReference property, object? value)
    {
        if (property.Metadata.IsDynamic)
        {
            return []; // .NET Validator does not support dynamically added properties
        }

        var validationContext = new ValidationContext(property.Subject)
        {
            MemberName = property.Name
        };

        var results = new List<ValidationResult>();
        Validator.TryValidateProperty(value, validationContext, results);
        return results;
    }
}
