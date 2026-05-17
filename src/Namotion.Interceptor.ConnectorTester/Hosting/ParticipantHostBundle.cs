using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.ConnectorTester.Logging;

namespace Namotion.Interceptor.ConnectorTester.Hosting;

/// <summary>
/// Bridges a participant's dedicated <see cref="IServiceProvider"/> into the main host's
/// hosted-service lifecycle. The bundle resolves all <see cref="IHostedService"/> instances
/// from the participant SP up front (so they're constructed deterministically before
/// <c>host.RunAsync()</c> starts) and starts/stops them in registration order, mirroring
/// what <see cref="IHost"/> would do. Each participant has its own SP with its own
/// <see cref="TaggingLoggerFactory"/>, so libraries that resolve <c>ILogger&lt;T&gt;</c>
/// inside this SP get participant-tagged categories.
/// </summary>
internal sealed class ParticipantHostBundle : IHostedService, IAsyncDisposable
{
    private readonly string _participantName;
    private readonly IServiceProvider _participantServiceProvider;
    private readonly IReadOnlyList<IHostedService> _hostedServices;
    private readonly ILogger _logger;

    public ParticipantHostBundle(string participantName, IServiceProvider participantServiceProvider)
    {
        _participantName = participantName;
        _participantServiceProvider = participantServiceProvider;
        _hostedServices = participantServiceProvider.GetServices<IHostedService>().ToList();
        _logger = participantServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<ParticipantHostBundle>();
    }

    /// <summary>Connectors resolved from this participant's SP, exposed for fault-target binding.</summary>
    public IEnumerable<ISubjectConnector> Connectors => _hostedServices.OfType<ISubjectConnector>();

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var started = 0;
        try
        {
            foreach (var service in _hostedServices)
            {
                await service.StartAsync(cancellationToken).ConfigureAwait(false);
                started++;
            }
        }
        catch (Exception startException)
        {
            // Partial failure: roll back the prefix that already started so connectors don't
            // leak background tasks or open sockets after we propagate the failure.
            _logger.LogError(startException,
                "Participant '{Participant}' startup failed on service {Index}/{Total}; stopping the {Started} already-started services in reverse order.",
                _participantName, started, _hostedServices.Count, started);

            for (var i = started - 1; i >= 0; i--)
            {
                try
                {
                    await _hostedServices[i].StopAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception stopException)
                {
                    _logger.LogError(stopException,
                        "Participant '{Participant}' failed to stop service {Index} during startup unwind; continuing.",
                        _participantName, i);
                }
            }
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        for (var i = _hostedServices.Count - 1; i >= 0; i--)
        {
            try
            {
                await _hostedServices[i].StopAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // Matches IHost.StopAsync's swallow-and-continue, but logs first so a connector
                // throwing during teardown doesn't disappear from the cycle log silently.
                _logger.LogError(exception,
                    "Participant '{Participant}' service {Index} threw during StopAsync; continuing teardown.",
                    _participantName, i);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_participantServiceProvider is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        }
        else if (_participantServiceProvider is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
