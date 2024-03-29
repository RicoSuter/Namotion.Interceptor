using System.ComponentModel.DataAnnotations;

namespace Namotion.Proxy.Validation;

public class DataAnnotationsValidator : IProxyPropertyValidator
{
    public IEnumerable<ValidationResult> Validate(IProxy proxy, string propertyName, object? value, IProxyContext context)
    {
        var results = new List<ValidationResult>();

        if (value is not null)
        {
            var validationContext = new ValidationContext(proxy)
            {
                MemberName = propertyName
            };

            Validator.TryValidateProperty(value, validationContext, results);
        }

        return results;
    }
}
