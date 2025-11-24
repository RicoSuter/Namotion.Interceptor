using System;
using System.Threading;
using System.Threading.Tasks;

namespace Namotion.Interceptor.Mqtt.Client;

internal sealed class MqttConnectionLifetime : IDisposable, IAsyncDisposable
{
    private readonly Func<ValueTask> _disposeAsync;
    private int _disposed;

    public MqttConnectionLifetime(Func<ValueTask> disposeAsync)
    {
        _disposeAsync = disposeAsync;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _disposeAsync().GetAwaiter().GetResult();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await _disposeAsync().ConfigureAwait(false);
        }
    }
}