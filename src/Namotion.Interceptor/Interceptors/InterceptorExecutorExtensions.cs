using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Interceptors;

/// <summary>
/// The declared-type write entry. It is an extension rather than a member of
/// <see cref="IInterceptorExecutor"/> because the core library targets a runtime without default
/// interface implementations, so a second interface member would be a breaking change for every
/// existing implementation.
/// </summary>
public static class InterceptorExecutorExtensions
{
    /// <summary>
    /// Writes a property whose declared type is exactly <typeparamref name="TProperty"/>, which is
    /// what a generated setter passes. Routing then reads the classification of
    /// <typeparamref name="TProperty"/>, a JIT constant, instead of looking up the declared
    /// property metadata on every write, which is what
    /// <see cref="IInterceptorExecutor.SetPropertyValue{TProperty}"/> has to do because its generic
    /// argument may be narrower or boxed.
    /// </summary>
    /// <remarks>
    /// Passing anything but the declared property type misroutes the write: a narrowed argument
    /// skips the structural protocol of a property that owns subjects, and a boxed one puts a
    /// scalar write through the topology gate. Anything that can widen or narrow, dynamic property
    /// setters among them, must use <see cref="IInterceptorExecutor.SetPropertyValue{TProperty}"/>,
    /// which classifies from the metadata and is correct for any generic argument.
    ///
    /// The cast is hard for the reason given on <see cref="IInterceptorExecutor"/>: the interface is
    /// not independently implementable, and the only executor a subject can publish is the one
    /// <see cref="InterceptorExecutor.GetOrCreate"/> hands it.
    /// </remarks>
    /// <param name="executor">The executor of the subject that owns the property.</param>
    /// <param name="propertyName">The name of the property to write.</param>
    /// <param name="newValue">The new value to set.</param>
    /// <param name="currentValue">The current value of the property.</param>
    /// <param name="writeValue">A delegate that writes the new value to the backing field.</param>
    /// <returns>True if the value was written; false if the write was suppressed by an interceptor.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool SetDeclaredPropertyValue<TProperty>(
        this IInterceptorExecutor executor,
        string propertyName,
        TProperty newValue,
        TProperty currentValue,
        Action<IInterceptorSubject, TProperty> writeValue)
    {
        return ((InterceptorExecutor)executor).SetDeclaredPropertyValue(propertyName, newValue, currentValue, writeValue);
    }
}
