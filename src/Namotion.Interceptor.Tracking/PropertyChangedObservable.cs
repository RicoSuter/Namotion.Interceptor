using System.Reactive.Subjects;
using Namotion.Interception.Lifecycle.Abstractions;
using Namotion.Interceptor;

namespace Namotion.Interception.Lifecycle;

public class PropertyChangedObservable : IObservable<ProxyPropertyChanged>, IWriteInterceptor
{
    private readonly IInterceptorCollection _context;
    private readonly Subject<ProxyPropertyChanged> _subject = new();

    public PropertyChangedObservable(IInterceptorCollection context)
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
