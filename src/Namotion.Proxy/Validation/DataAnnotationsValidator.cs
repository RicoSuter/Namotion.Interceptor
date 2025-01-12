using System.ComponentModel.DataAnnotations;
using Namotion.Interceptor;

namespace Namotion.Proxy.Validation;

public class DataAnnotationsValidator : IProxyPropertyValidator
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
