using System.ComponentModel.DataAnnotations;

namespace Namotion.Interceptor.Validation;

public class DataAnnotationsValidator : IPropertyValidator
{
    public IEnumerable<ValidationResult> Validate(PropertyReference property, object? value)
    {
        var results = new List<ValidationResult>();

        if (value is not null)
        {
            var validationContext = new ValidationContext(property.Subject)
            {
                MemberName = property.Name
            };

            Validator.TryValidateProperty(value, validationContext, results);
        }

        return results;
    }
}
