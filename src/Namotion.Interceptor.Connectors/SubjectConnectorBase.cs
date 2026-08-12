using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Connectors.Diagnostics;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Abstract base for every connector, client or server, owning the diagnostics lifecycle so that a
/// connector cannot forget to report that it stopped serving.
/// </summary>
/// <remarks>
/// <see cref="ExecuteAsync"/> is sealed and derived classes override <see cref="RunAsync"/> instead.
/// Without that, each connector's own loop would have to force liveness false on fault, on exit and
/// on disposal, and a connector whose loop faulted would keep reporting that it was serving.
/// </remarks>
public abstract class SubjectConnectorBase : BackgroundService, ISubjectConnector
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SubjectConnectorBase"/> class.
    /// </summary>
    /// <param name="metrics">
    /// The metrics this connector writes to. Created by the caller and passed in rather than created
    /// here, so a derived class can supply a richer type and still hand the same instance to its own
    /// diagnostics view.
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
            // Inside the try, because it calls Reset on every registered resettable and both that
            // interface and its registration are public: a third-party Reset that throws would
            // otherwise fault this method with neither the error recorded nor liveness forced false.
            Metrics.MarkStarted();

            await RunAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // An expected shutdown is not a fault, and recording it would overwrite the genuine error
            // that made the connector fail, which stays sticky until the next MarkStarted. Connectors
            // back off with a delay on the stopping token from inside their own catch blocks, so a
            // stop landing during a backoff throws the cancellation out of RunAsync rather than out of
            // the sibling clause that would have swallowed it.
            throw;
        }
        catch (Exception exception)
        {
            // A cancellation the stopping token did not cause lands here and is recorded, because it
            // is a genuine fault rather than a shutdown.
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
        // above runs at an unspecified later time. Without this, a disposed connector keeps reporting
        // that it is serving.
        Metrics.MarkStopped();
        base.Dispose();
    }
}
