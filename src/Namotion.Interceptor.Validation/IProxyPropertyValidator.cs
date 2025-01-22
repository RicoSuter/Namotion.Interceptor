using System.ComponentModel.DataAnnotations;

namespace Namotion.Interceptor.Validation;

public interface IProxyPropertyValidator
{
    IEnumerable<ValidationResult> Validate(PropertyReference property, object? value);
}

