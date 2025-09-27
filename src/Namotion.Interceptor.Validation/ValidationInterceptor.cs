using System.ComponentModel.DataAnnotations;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Validation;

public class ValidationInterceptor : IWriteInterceptor
{
    public void WriteProperty<TProperty>(ref WritePropertyInterception<TProperty> context, WriteInterceptionAction<TProperty> next)
    {
        // TODO(perf): Avoid linq to avoid ref copy
        var interception = context;
        var errors = context.Property
            .Subject
            .Context
            .GetServices<IPropertyValidator>()
            .SelectMany(v => v.Validate(interception.Property, interception.NewValue))
            .ToArray();

        if (errors.Any())
        {
            throw new ValidationException(string.Join("\n", errors.Select(e => e.ErrorMessage)));
        }

        next(ref context);
    }
}
