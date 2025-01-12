using Namotion.Proxy.Abstractions;

using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
using Namotion.Interceptor;

namespace Namotion.Proxy.Validation;

public class ProxyValidationHandler : IWriteInterceptor
{
    private readonly Lazy<IProxyPropertyValidator[]> _propertyValidators;

    public ProxyValidationHandler(Lazy<IProxyPropertyValidator[]> propertyValidators)
    {
        _propertyValidators = propertyValidators;
    }

    public void WriteProperty(WritePropertyInterception context, Action<WritePropertyInterception> next)
    {
        var errors = _propertyValidators.Value
            .SelectMany(v => v.Validate(context.Property, context.NewValue))
            .ToArray();

        if (errors.Any())
        {
            throw new ValidationException(string.Join("\n", errors.Select(e => e.ErrorMessage)));
        }

        next(context);
    }
}
