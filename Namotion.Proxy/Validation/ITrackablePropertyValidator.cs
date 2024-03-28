using Namotion.Proxy;
using Namotion.Proxy.Abstractions;
using System.ComponentModel.DataAnnotations;

namespace Namotion.Proxy.Validation;

public interface ITrackablePropertyValidator : IProxyHandler
{
    IEnumerable<ValidationResult> Validate(IProxy proxy, string propertyName, object? value, IProxyContext context);
}

