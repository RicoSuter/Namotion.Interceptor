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

    public object? WriteProperty(WritePropertyInterception context, Func<WritePropertyInterception, object?> next)
    {
        var errors = _propertyValidators
            .SelectMany(v => v.Validate(context.Property, context.NewValue))
            .ToArray();

        if (errors.Any())
        {
            throw new ValidationException(string.Join("\n", errors.Select(e => e.ErrorMessage)));
        }

        return next(context);
    }
}
