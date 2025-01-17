using System.Reactive.Subjects;
using Namotion.Interception.Lifecycle.Abstractions;
using Namotion.Interceptor;

namespace Namotion.Interception.Lifecycle;

public class PropertyChangedObservable : IObservable<PropertyChangedContext>, IWriteInterceptor
{
    private readonly Subject<PropertyChangedContext> _subject = new();
    
    public object? WriteProperty(WritePropertyInterception context, Func<WritePropertyInterception, object?> next)
    {
        var currentValue = context.CurrentValue;
        var result = next(context); 

        var changedContext = new PropertyChangedContext(context.Property, currentValue, result);
        _subject.OnNext(changedContext);
     
        return result;
    }

    public IDisposable Subscribe(IObserver<PropertyChangedContext> observer)
    {
        return _subject.Subscribe(observer);
    }
}
