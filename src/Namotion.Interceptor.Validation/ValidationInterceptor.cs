using System.ComponentModel.DataAnnotations;
using System.Runtime.CompilerServices;
using System.Text;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Validation;

public class ValidationInterceptor : IWriteInterceptor
{
    public bool ShouldInterceptWrite
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
        var validators = context.Property.Subject.Context.GetServices<IPropertyValidator>();
        
        List<ValidationResult>? additionalErrors = null;
        foreach (var validator in validators)
        {
            foreach (var error in validator.Validate(context.Property, context.NewValue))
            {
                additionalErrors ??= [];
                additionalErrors.Add(error);
            }
        }

        if (additionalErrors is not null)
        {
            var sb = new StringBuilder();
            foreach (var error in additionalErrors)
            {
                sb.Append('\n');
                sb.Append(error.ErrorMessage);
            }
            
            throw new ValidationException(sb.ToString());
        }

        next(ref context);
    }
}
