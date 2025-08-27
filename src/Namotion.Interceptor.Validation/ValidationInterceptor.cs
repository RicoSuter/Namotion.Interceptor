using System.ComponentModel.DataAnnotations;

namespace Namotion.Interceptor.Validation;

public class ValidationInterceptor : IWriteInterceptor
{
    public void WriteProperty<TProperty>(ref WritePropertyInterception<TProperty> context, Action<WritePropertyInterception<TProperty>> next)
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

        next(context);
    }
    
    public void AttachTo(IInterceptorSubject subject)
    {
    }

    public void DetachFrom(IInterceptorSubject subject)
    {
    }
}
