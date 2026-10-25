using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Adds OpenTelemetry, optional Seq export and health checks to HomeBlaze.
/// Based on the Aspire service defaults template.
/// </summary>
public static class Extensions
{
    private const string HealthEndpointPath = "/health";
    private const string AlivenessEndpointPath = "/alive";
    private const string SeqConnectionName = "seq";

    /// <summary>
    /// Adds OpenTelemetry, optional Seq export and the default health checks.
    /// </summary>
    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        var useOtlpExporter = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
        var useSeq = !string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString(SeqConnectionName));

        builder.ConfigureOpenTelemetry(useOtlpExporter, useSeq);
        builder.AddDefaultHealthChecks();

        if (useSeq)
        {
            // The Seq health check is disabled: the optional log server must not make HomeBlaze unhealthy.
            builder.AddSeqEndpoint(SeqConnectionName, settings => settings.DisableHealthChecks = true);
        }

        // No HTTP client resilience handler or service discovery: device clients keep their own timeouts and retries.

        return builder;
    }

    /// <summary>
    /// Collects logs and metrics, and traces only when an OTLP endpoint or Seq is configured.
    /// Exports logs, metrics and traces over OTLP when <paramref name="useOtlpExporter"/> is <c>true</c>.
    /// </summary>
    private static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder, bool useOtlpExporter, bool useSeq) where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        var openTelemetry = builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();
            });

        if (useOtlpExporter || useSeq)
        {
            // Without an exporter, the default sampler still records an Activity per request and outgoing call,
            // so tracing is only registered when something consumes it. Seq adds its own trace processor here.
            openTelemetry.WithTracing(tracing =>
            {
                tracing.AddSource(builder.Environment.ApplicationName)
                    .AddAspNetCoreInstrumentation(options =>
                        options.Filter = context =>
                            !context.Request.Path.StartsWithSegments(HealthEndpointPath)
                            && !context.Request.Path.StartsWithSegments(AlivenessEndpointPath))
                    .AddHttpClientInstrumentation();
            });
        }

        if (useOtlpExporter)
        {
            openTelemetry.UseOtlpExporter();
        }

        return builder;
    }

    /// <summary>
    /// Adds a liveness check tagged <c>live</c>.
    /// </summary>
    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }

    /// <summary>
    /// Maps <c>/health</c> (all checks) and <c>/alive</c> (checks tagged <c>live</c>) in every environment.
    /// The endpoints report status only.
    /// </summary>
    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        app.MapHealthChecks(HealthEndpointPath);
        app.MapHealthChecks(AlivenessEndpointPath, new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains("live")
        });

        return app;
    }
}
