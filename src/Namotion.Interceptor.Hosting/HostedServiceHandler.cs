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

    public void OnLifecycleEvent(SubjectLifecycleChange change)
    {
        _logger ??= _loggerResolver();

        if (change.IsAttached)
        {
            if (change.Subject is IHostedService hostedService)
            {
                AttachHostedService(hostedService);
            }

            foreach (var hostedService2 in change.Subject.GetAttachedHostedServices())
            {
                AttachHostedService(hostedService2);
            }
        }
        else if (change.IsDetached)
        {
            if (change.Subject is IHostedService hostedService)
            {
                DetachHostedService(hostedService);
            }

            foreach (var attachedHostedService in change.Subject.GetAttachedHostedServices())
            {
                change.Subject.DetachHostedService(attachedHostedService);
            }
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
            
            Task[] tasks;
            lock (_hostedServices)
            {
                tasks = _hostedServices
                    .Select(async hostedService =>
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
                    })
                    .ToArray();
                
                _hostedServices.Clear();
            }
            
            await Task.WhenAll(tasks);
        }
        finally
        {
            await _executeTask
                .WaitAsync(cancellationToken)
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }
    
    internal void AttachHostedService(IHostedService hostedService)
    {
        lock (_hostedServices)
        {
            if (_hostedServices.Add(hostedService))
            {
                PostStartService(hostedService, null);
            }
        }
    }

    internal void DetachHostedService(IHostedService hostedService)
    {
        lock (_hostedServices)
        {
            if (_hostedServices.Remove(hostedService))
            {
                PostStopService(hostedService, null);
            }
        }
    }

    internal async Task AttachHostedServiceAsync(IHostedService hostedService, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource();
        lock (_hostedServices)
        {
            if (_hostedServices.Add(hostedService))
            {
                PostStartService(hostedService, tcs);
            }
            else
            {
                tcs.TrySetResult(); // Already attached
            }
        }

        await tcs.Task.WaitAsync(cancellationToken);
    }
    
    internal async Task DetachHostedServiceAsync(IHostedService hostedService, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource();
        lock (_hostedServices)
        {
            if (_hostedServices.Remove(hostedService))
            {
                PostStopService(hostedService, tcs);
            }
            else
            {
                tcs.TrySetResult(); // Already removed
            }
        }

        await tcs.Task.WaitAsync(cancellationToken);
    }

    private void PostStartService(IHostedService hostedService, TaskCompletionSource? tcs)
    {
        _actions.Post(async token =>
        {
            try
            {
                await Task.Delay(50, token); // TODO: Fix small delay to let sync property assignments/deserialization complete

                _logger?.LogInformation("Starting attached hosted service {Service}.", hostedService.ToString());
                await hostedService.StartAsync(token);
                tcs?.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs?.TrySetException(ex);
            }
        });
    }

    private void PostStopService(IHostedService hostedService, TaskCompletionSource? tcs)
    {
        _actions.Post(async token =>
        {
            try
            {
                await Task.Delay(50, token); // TODO: Fix small delay to let sync property assignments/deserialization complete

                _logger?.LogInformation("Stopping detached hosted service {Service}.", hostedService.ToString());
                await hostedService.StopAsync(token);
                tcs?.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs?.TrySetException(ex);
            }
        });
    }

    public void Dispose()
    {
        _stoppingCts?.Cancel();
    }
}
