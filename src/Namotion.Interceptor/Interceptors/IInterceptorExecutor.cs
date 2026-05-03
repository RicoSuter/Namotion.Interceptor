namespace Namotion.Interceptor.Interceptors;

public interface IInterceptorExecutor : IInterceptorSubjectContext
{
    /// <summary>
    /// Gets a property value through the interceptor chain.
    /// </summary>
    /// <param name="propertyName">The name of the property to read.</param>
    /// <param name="readValue">A delegate that reads the backing field value from the subject.</param>
    TProperty GetPropertyValue<TProperty>(string propertyName, Func<IInterceptorSubject, TProperty> readValue);

    /// <summary>
    /// Sets a property value through the interceptor chain with the current value already known.
    /// </summary>
    /// <param name="propertyName">The name of the property to write.</param>
    /// <param name="newValue">The new value to set.</param>
    /// <param name="currentValue">The current value of the property.</param>
    /// <param name="writeValue">A delegate that writes the new value to the backing field.</param>
    /// <returns>True if the value was written; false if the write was suppressed by an interceptor.</returns>
    bool SetPropertyValue<TProperty>(string propertyName, TProperty newValue, TProperty currentValue, Action<IInterceptorSubject, TProperty> writeValue);

    /// <summary>
    /// Invokes a method through the interceptor chain.
    /// </summary>
    /// <param name="methodName">The name of the method to invoke.</param>
    /// <param name="parameters">The method parameters.</param>
    /// <param name="invokeMethod">A delegate that performs the actual method invocation on the subject.</param>
    /// <returns>The return value of the method invocation.</returns>
    object? InvokeMethod(string methodName, object?[] parameters, Func<IInterceptorSubject, object?[], object?> invokeMethod);
}