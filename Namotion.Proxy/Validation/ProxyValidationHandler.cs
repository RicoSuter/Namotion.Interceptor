using Namotion.Proxy.Abstractions;
using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;

namespace Namotion.Proxy.Validation;

public class ProxyValidationHandler : IProxyWriteHandler
{
    private readonly Lazy<ITrackablePropertyValidator[]> _propertyValidators;

    public ProxyValidationHandler(Lazy<ITrackablePropertyValidator[]> propertyValidators)
    {
        _propertyValidators = propertyValidators;
    }

    public void SetProperty(WriteProxyPropertyContext context, Action<WriteProxyPropertyContext> next)
    {
        var errors = _propertyValidators.Value
            .SelectMany(v => v.Validate(context.Property.Proxy, context.Property.Name, context.NewValue, context.Context))
            .ToArray();

        if (errors.Any())
        {
            throw new ValidationException(string.Join("\n", errors.Select(e => e.ErrorMessage)));
        }

        next(context);
    }
}
