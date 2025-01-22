using System.ComponentModel.DataAnnotations;

namespace Namotion.Interceptor.Validation;

public interface IPropertyValidator
{
    IEnumerable<ValidationResult> Validate(PropertyReference property, object? value);
}

