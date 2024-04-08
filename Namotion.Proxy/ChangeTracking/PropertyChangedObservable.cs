using Namotion.Proxy.Abstractions;
using System.Reactive.Subjects;

namespace Namotion.Proxy.ChangeTracking;

internal class PropertyChangedObservable : IObservable<ProxyPropertyChanged>, IProxyWriteHandler, IProxyDerivedPropertyChangedHandler
{
    private Subject<ProxyPropertyChanged> _subject = new();

    public void WriteProperty(WriteProxyPropertyContext context, Action<WriteProxyPropertyContext> next)
    {
        var currentValue = context.CurrentValue;
        var newValue = context.NewValue;

        next(context);

        var changedContext = new ProxyPropertyChanged(context.Property, currentValue, newValue, context.Context);
        _subject.OnNext(changedContext);
    }

    public IDisposable Subscribe(IObserver<ProxyPropertyChanged> observer)
    {
        return _subject.Subscribe(observer);
    }

    public void OnDerivedPropertyChanged(ProxyPropertyChanged changedContext)
    {
        _subject.OnNext(changedContext);
    }
}
