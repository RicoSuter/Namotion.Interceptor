using Namotion.Proxy.Abstractions;

using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
using Namotion.Interceptor;

namespace Namotion.Proxy.Validation;

public class ValidationInterceptor : IWriteInterceptor
{
    private readonly IProxyPropertyValidator[] _propertyValidators;

    public ValidationInterceptor(IEnumerable<IProxyPropertyValidator> propertyValidators)
    {
        _propertyValidators = propertyValidators.ToArray();
    }

    public void WriteProperty(WritePropertyInterception context, Action<WritePropertyInterception> next)
    {
        var errors = _propertyValidators
            .SelectMany(v => v.Validate(context.Property, context.NewValue))
            .ToArray();

        if (errors.Any())
        {
            throw new ValidationException(string.Join("\n", errors.Select(e => e.ErrorMessage)));
        }

        next(context);
    }
}
