namespace Namotion.Interceptor.Validation;

public static class InterceptorCollectionExtensions
{
    /// <summary>
    /// Registers support for <see cref="IPropertyValidator"/> handlers.
    /// </summary>
    /// <param name="context">The collection.</param>
    /// <returns>The collection.</returns>
    public static IInterceptorSubjectContext WithPropertyValidation(this IInterceptorSubjectContext context)
    {
        return context
            .WithInterceptor(() => new ValidationInterceptor());
    }

    /// <summary>
    /// Adds support for data annotations on the subject's properties and <see cref="WithPropertyValidation"/>.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <returns>The builder.</returns>
    public static IInterceptorSubjectContext WithDataAnnotationValidation(this IInterceptorSubjectContext builder)
    {
        builder
            .WithPropertyValidation()
            .TryAddService(() => new DataAnnotationsValidator(), _ => true);

        return builder;
    }
}