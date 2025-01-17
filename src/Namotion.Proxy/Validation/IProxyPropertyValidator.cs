using System.ComponentModel.DataAnnotations;
using Namotion.Interceptor;

namespace Namotion.Proxy.Validation;

public interface IProxyPropertyValidator
{
    IEnumerable<ValidationResult> Validate(PropertyReference property, object? value);
}

