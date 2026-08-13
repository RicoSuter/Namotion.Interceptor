using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Connectors.Diagnostics;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Abstract base for every connector, client or server, owning the diagnostics lifecycle so that a
/// connector cannot forget to report that it stopped serving.
/// </summary>
/// <remarks>
/// <see cref="ExecuteAsync"/> is sealed and derived classes override <see cref="RunAsync"/> instead.
/// </remarks>
public abstract class SubjectConnectorBase : BackgroundService, ISubjectConnector
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SubjectConnectorBase"/> class.
    /// </summary>
    /// <param name="metrics">
    /// The metrics this connector writes to. Passed in rather than created here, so a derived class can
    /// supply a richer type and still hand the same instance to its own diagnostics view.
    /// </param>
    protected SubjectConnectorBase(ConnectorMetrics metrics)
    {
        Metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
    }

    /// <summary>
    /// Gets the write side of this connector's diagnostics. Never exposed through
    /// <see cref="ISubjectConnector"/>.
    /// </summary>
    protected ConnectorMetrics Metrics { get; }

    /// <inheritdoc cref="ISubjectConnector.RootSubject" />
    public abstract IInterceptorSubject RootSubject { get; }

    /// <summary>
    /// Gets what this connector reports about its transport.
    /// </summary>
    public abstract ConnectorDiagnostics Diagnostics { get; }

    /// <summary>
    /// Runs the connector until cancellation. Replaces <see cref="ExecuteAsync"/>, which this class
    /// seals so that the diagnostics lifecycle is applied uniformly.
    /// </summary>
    protected abstract Task RunAsync(CancellationToken stoppingToken);

    /// <inheritdoc />
    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Inside the try, because it calls Reset on every registered resettable and that
            // registration is public: a third-party Reset that throws must still be recorded.
            Metrics.MarkStarted();

            await RunAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // An expected shutdown is not a fault, and recording it would overwrite the genuine error
            // that made the connector fail, which stays sticky until the next MarkStarted.
            throw;
        }
        catch (Exception exception)
        {
            // A cancellation the stopping token did not cause is a genuine fault and is recorded here.
            Metrics.ReportError(exception);
            throw;
        }
        finally
        {
            Metrics.MarkStopped();
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        // BackgroundService.Dispose cancels the token but does not await ExecuteAsync, so the finally
        // above runs at an unspecified later time.
        Metrics.MarkStopped();
        base.Dispose();
    }
}
