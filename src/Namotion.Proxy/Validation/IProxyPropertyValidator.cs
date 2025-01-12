using Namotion.Proxy.Abstractions;

using System.ComponentModel.DataAnnotations;
using Namotion.Interceptor;

namespace Namotion.Proxy.Validation;

public interface IProxyPropertyValidator : IProxyHandler
{
    IEnumerable<ValidationResult> Validate(PropertyReference property, object? value);
}

