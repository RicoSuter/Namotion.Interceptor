using Microsoft.Extensions.Logging;
using Namotion.Interceptor;

namespace HomeBlaze.Virtual;

/// <summary>The getter and setter of a binding property.</summary>
internal sealed class VirtualBinding(string path, VirtualDataType dataType, IInterceptorSubject scope, VirtualRuntime runtime)
{
    // The bindings that this thread writes, innermost last. The registry guards only getters, so bindings that point at
    // each other would otherwise write each other until the stack overflows.
    [ThreadStatic]
    private static List<VirtualBinding>? _writing;

    /// <summary>Reads the target, which records it as a dependency. Never throws.</summary>
    public object? Read(IInterceptorSubject subject)
    {
        try
        {
            var target = VirtualExpression.ResolveProperty(runtime.Resolver, path, scope);
            return target is null ? null : VirtualValueConverter.ToDataType(target.GetValue(), dataType);
        }
        catch (Exception exception)
        {
            runtime.Logger.LogDebug(exception, "The virtual property binding to {Path} failed to read.", path);
            return null;
        }
    }

    /// <summary>Writes the value to the target, converted to its type.</summary>
    /// <exception cref="InvalidOperationException">
    /// The target is missing, cannot be written or does not take the value, or the binding writes itself through other bindings.
    /// </exception>
    public void Write(IInterceptorSubject subject, object? value)
    {
        var writing = _writing ??= [];
        if (writing.Contains(this))
        {
            throw new InvalidOperationException($"The binding to {path} writes itself through other bindings.");
        }

        writing.Add(this);
        try
        {
            WriteTarget(value);
        }
        finally
        {
            writing.RemoveAt(writing.Count - 1);
        }
    }

    private void WriteTarget(object? value)
    {
        var target = VirtualExpression.ResolveProperty(runtime.Resolver, path, scope)
            ?? throw new InvalidOperationException($"The path {path} leads to no readable property.");
        if (!target.HasPublicSetter || target.CanContainSubjects)
        {
            throw new InvalidOperationException($"The property at {path} cannot be written.");
        }

        if (!VirtualValueConverter.TryConvertToTarget(value, target.Type, out var converted))
        {
            throw new InvalidOperationException($"The value cannot be converted to the type of the property at {path}.");
        }

        target.SetValue(converted);
    }
}
