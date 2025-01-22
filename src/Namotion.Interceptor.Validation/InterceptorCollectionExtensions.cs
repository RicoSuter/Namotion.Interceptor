namespace Namotion.Interceptor.Validation;

public static class InterceptorCollectionExtensions
{
    /// <summary>
    /// Registers support for <see cref="IPropertyValidator"/> handlers.
    /// </summary>
    /// <param name="collection">The collection.</param>
    /// <returns>The collection.</returns>
    public static IInterceptorCollection WithPropertyValidation(this IInterceptorCollection collection)
    {
        return collection
            .WithInterceptor(() => new ValidationInterceptor(collection));
    }

    /// <summary>
    /// Adds support for data annotations on the subject's properties and <see cref="WithPropertyValidation"/>.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <returns>The builder.</returns>
    public static IInterceptorCollection WithDataAnnotationValidation(this IInterceptorCollection builder)
    {
        builder
            .WithPropertyValidation()
            .TryAddService<IPropertyValidator, DataAnnotationsValidator>(() => new DataAnnotationsValidator());

        return builder;
    }
}