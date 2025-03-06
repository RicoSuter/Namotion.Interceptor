using System.Threading.Tasks.Dataflow;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Hosting;

internal class HostedServiceHandler : IHostedService, ILifecycleHandler, IDisposable
{
    private ILogger? _logger;

    private Task? _executeTask;
    private CancellationTokenSource? _stoppingCts;

    private readonly Func<ILogger?> _loggerResolver;
    private readonly BufferBlock<Func<CancellationToken, Task>> _actions = new();
    private readonly HashSet<IHostedService> _hostedServices = [];

    public HostedServiceHandler(Func<ILogger?> loggerResolver)
    {
        _loggerResolver = loggerResolver;
    }

    public void Attach(LifecycleContext context)
    {
        _logger ??= _loggerResolver();

        if (context is { ReferenceCount: 1, Subject: IHostedService hostedService })
        {
            AttachHostedService(hostedService);
        }
    }

    public void Detach(LifecycleContext context)
    {
        _logger ??= _loggerResolver();

        if (context is { ReferenceCount: 0, Subject: IHostedService hostedService })
        {
            DetachHostedService(hostedService);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_executeTask is not null)
        {
            return _executeTask.IsCompleted ? _executeTask : Task.CompletedTask;
        }
        
        _stoppingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _executeTask = ExecuteAsync(_stoppingCts.Token);
        return _executeTask.IsCompleted ? _executeTask : Task.CompletedTask;
    }

    internal void AttachHostedService(IHostedService hostedService)
    {
        if (_hostedServices.Add(hostedService))
        {
            _actions.Post(token =>
            {
                _logger?.LogInformation("Starting attached hosted service {Service}.", hostedService.ToString());
                return hostedService.StartAsync(token);
            });
        }
    }

    internal void DetachHostedService(IHostedService hostedService)
    {
        if (_hostedServices.Remove(hostedService))
        {
            _actions.Post(token =>
            {
                _logger?.LogInformation("Stopping detached hosted service {Service}.", hostedService.ToString());
                return hostedService.StopAsync(token);
            });
        }
    }

    private async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger ??= _loggerResolver();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var action = await _actions.ReceiveAsync(stoppingToken);
                await action(stoppingToken);
            }
            catch (Exception exception)
            {
                if (exception is not OperationCanceledException)
                {
                    _logger?.LogError(exception, "Failed to execute hosted service action.");
                }
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_executeTask == null)
        {
            return;
        }

        try
        {
            if (_stoppingCts is not null)
            {
                await _stoppingCts.CancelAsync();
            }
            
            await Task.WhenAll(_hostedServices.Select(async hostedService =>
            {
                try
                {
                    _logger?.LogInformation("Stopping hosted service {Service}.", hostedService.ToString());
                    await hostedService.StopAsync(cancellationToken);
                }
                catch (Exception exception)
                {
                    if (exception is not OperationCanceledException)
                    {
                        _logger?.LogError(exception, "Failed to stop hosted service {Service}.", hostedService.ToString());
                    }
                }
            }));
        }
        finally
        {
            await _executeTask
                .WaitAsync(cancellationToken)
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }
    
    public void Dispose()
    {
        _stoppingCts?.Cancel();
    }
}
