using Microsoft.Extensions.Logging;
using Namotion.Interceptor;

namespace HomeBlaze.Virtual;

/// <summary>The getter and setter of a binding property.</summary>
internal sealed class VirtualBinding(string path, VirtualDataType dataType, IInterceptorSubject scope, VirtualRuntime runtime)
{
    public VirtualMisses Misses { get; } = new(runtime.Awaiter);

    /// <summary>Reads the target, which records it as a dependency. Never throws.</summary>
    public object? Read(IInterceptorSubject subject)
    {
        try
        {
            List<VirtualMiss>? misses = null;
            var target = VirtualExpression.ResolveProperty(runtime.Resolver, path, scope, ref misses);
            Misses.Record(misses);
            return target is null ? null : VirtualValueConverter.ToDataType(target.GetValue(), dataType);
        }
        catch (Exception exception)
        {
            runtime.Logger.LogDebug(exception, "The virtual property binding to {Path} failed to read.", path);
            return null;
        }
    }

    /// <summary>Writes the value to the target, converted to its type.</summary>
    /// <exception cref="InvalidOperationException">The target is missing, cannot be written or does not take the value.</exception>
    public void Write(IInterceptorSubject subject, object? value)
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
