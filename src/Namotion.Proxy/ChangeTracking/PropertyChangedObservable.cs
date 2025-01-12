using Namotion.Proxy.Abstractions;
using System.Reactive.Subjects;
using Namotion.Interceptor;

namespace Namotion.Proxy.ChangeTracking;

internal class PropertyChangedObservable : IObservable<ProxyPropertyChanged>, IWriteInterceptor
{
    private readonly Subject<ProxyPropertyChanged> _subject = new();

    public void WriteProperty(WritePropertyInterception context, Action<WritePropertyInterception> next)
    {
        var currentValue = context.CurrentValue;
        var newValue = context.NewValue;

        next(context); 
        
        // TODO: Should retrieve actual new value

        var changedContext = new ProxyPropertyChanged(context.Property, currentValue, newValue, (IProxyContext)context.Context);
        _subject.OnNext(changedContext);
    }

    public IDisposable Subscribe(IObserver<ProxyPropertyChanged> observer)
    {
        return _subject.Subscribe(observer);
    }
}
