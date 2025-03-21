using System.ComponentModel.DataAnnotations;

namespace Namotion.Interceptor.Validation;

public class DataAnnotationsValidator : IPropertyValidator
{
    public IEnumerable<ValidationResult> Validate(PropertyReference property, object? value)
    {
        var validationContext = new ValidationContext(property.Subject)
        {
            MemberName = property.Name
        };

        var results = new List<ValidationResult>();
        Validator.TryValidateProperty(value, validationContext, results);
        return results;
    }
}
