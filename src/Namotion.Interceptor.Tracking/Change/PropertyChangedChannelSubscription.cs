using System.Threading.Channels;

namespace Namotion.Interceptor.Tracking.Change;

public sealed class PropertyChangedChannelSubscription : IDisposable
{
    private readonly PropertyChangedChannel _channel;
    private readonly Channel<SubjectPropertyChange> _readerChannel;
    private int _disposed;

    public PropertyChangedChannelSubscription(PropertyChangedChannel channel, Channel<SubjectPropertyChange> readerChannel)
    {
        _channel = channel;
        _readerChannel = readerChannel;
    }

    public ChannelReader<SubjectPropertyChange> Reader => _readerChannel.Reader;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _channel.Unsubscribe(_readerChannel);
        }
    }
}