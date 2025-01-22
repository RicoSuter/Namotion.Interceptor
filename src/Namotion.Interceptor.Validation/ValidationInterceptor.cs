using System.ComponentModel.DataAnnotations;

namespace Namotion.Interceptor.Validation;

public class ValidationInterceptor : IWriteInterceptor
{
    private readonly IInterceptorCollection _collection;

    public ValidationInterceptor(IInterceptorCollection collection)
    {
        _collection = collection;
    }

    public object? WriteProperty(WritePropertyInterception context, Func<WritePropertyInterception, object?> next)
    {
        var errors = _collection
            .GetServices<IPropertyValidator>()
            .SelectMany(v => v.Validate(context.Property, context.NewValue))
            .ToArray();

        if (errors.Any())
        {
            throw new ValidationException(string.Join("\n", errors.Select(e => e.ErrorMessage)));
        }

        return next(context);
    }
    
    public void AttachTo(IInterceptorSubject subject)
    {
    }

    public void DetachFrom(IInterceptorSubject subject)
    {
    }
}
