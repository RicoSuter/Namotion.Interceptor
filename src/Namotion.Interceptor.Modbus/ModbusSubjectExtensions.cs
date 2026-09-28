using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Modbus;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Creates and registers Modbus client sources.
/// </summary>
public static class ModbusSubjectExtensions
{
    /// <summary>
    /// Creates a Modbus client source for <paramref name="subject"/>. Start it as a hosted service, for example with
    /// <c>AttachHostedServiceAsync</c>, and dispose it when done.
    /// </summary>
    /// <exception cref="ArgumentException">A <paramref name="configuration"/> value is out of range.</exception>
    /// <exception cref="InvalidOperationException">The subject's context has no lifecycle tracking (<c>WithLifecycle()</c>).</exception>
    public static ModbusSubjectClientSource CreateModbusClientSource(
        this IInterceptorSubject subject, ModbusClientConfiguration configuration, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);
        return new ModbusSubjectClientSource(subject, configuration, logger);
    }
}
