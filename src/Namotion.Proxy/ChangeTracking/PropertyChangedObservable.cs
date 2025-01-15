using Namotion.Proxy.Abstractions;
using Namotion.Interceptor;
using System.Reactive.Subjects;

namespace Namotion.Proxy.ChangeTracking;

internal class PropertyChangedObservable : IObservable<ProxyPropertyChanged>, IWriteInterceptor
{
    private readonly IProxyContext _context;
    private readonly Subject<ProxyPropertyChanged> _subject = new();

    public PropertyChangedObservable(IProxyContext context)
    {
        _context = context;
    }
    
    public void WriteProperty(WritePropertyInterception context, Action<WritePropertyInterception> next)
    {
        var currentValue = context.CurrentValue;
        var newValue = context.NewValue;

        next(context); 
        
        // TODO: Should retrieve actual new value

        var changedContext = new ProxyPropertyChanged(context.Property, currentValue, newValue, _context);
        _subject.OnNext(changedContext);
    }

    public IDisposable Subscribe(IObserver<ProxyPropertyChanged> observer)
    {
        return _subject.Subscribe(observer);
    }
}
