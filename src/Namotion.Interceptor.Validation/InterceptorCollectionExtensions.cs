namespace Namotion.Interceptor.Validation;

public static class InterceptorCollectionExtensions
{
    /// <summary>
    /// Registers support for <see cref="IProxyPropertyValidator"/> handlers.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <returns>The builder.</returns>
    public static IInterceptorCollection WithPropertyValidation(this IInterceptorCollection builder)
    {
        return builder
            .WithInterceptor(() => new ValidationInterceptor(builder.GetServices<IProxyPropertyValidator>()));
    }

    /// <summary>
    /// Adds support for data annotations on the interceptable properties and <see cref="WithPropertyValidation"/>.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <returns>The builder.</returns>
    public static IInterceptorCollection WithDataAnnotationValidation(this IInterceptorCollection builder)
    {
        builder
            .WithPropertyValidation()
            .TryAddService<IProxyPropertyValidator, DataAnnotationsValidator>(() => new DataAnnotationsValidator());

        return builder;
    }
}