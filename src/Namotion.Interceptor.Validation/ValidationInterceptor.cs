using System.ComponentModel.DataAnnotations;

namespace Namotion.Interceptor.Validation;

public class ValidationInterceptor : IWriteInterceptor
{
    public void WriteProperty<TProperty>(WritePropertyInterception<TProperty> context, Action<WritePropertyInterception<TProperty>> next)
    {
        var errors = context.Property
            .Subject
            .Context
            .GetServices<IPropertyValidator>()
            .SelectMany(v => v.Validate(context.Property, context.NewValue))
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
