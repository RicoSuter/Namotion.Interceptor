using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Opc.Ua;

namespace Namotion.Interceptor.OpcUa;

/// <summary>
/// A lightweight null implementation of ITelemetryContext for testing and scenarios
/// where no telemetry is needed. Uses NullLoggerFactory for minimal overhead.
/// </summary>
public sealed class NullTelemetryContext : ITelemetryContext
{
    /// <summary>
    /// Singleton instance of the null telemetry context.
    /// </summary>
    public static readonly NullTelemetryContext Instance = new();

    private static readonly ActivitySource NullActivitySource = new("Namotion.Interceptor.OpcUa.Null");

    private NullTelemetryContext() { }

    /// <inheritdoc />
    public ILoggerFactory LoggerFactory => NullLoggerFactory.Instance;

    /// <inheritdoc />
    public ActivitySource ActivitySource => NullActivitySource;

    /// <inheritdoc />
    public Meter CreateMeter() => new Meter("Namotion.Interceptor.OpcUa.Null");
}
