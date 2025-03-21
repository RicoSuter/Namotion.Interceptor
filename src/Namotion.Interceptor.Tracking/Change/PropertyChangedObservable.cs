using System.Reactive.Subjects;

namespace Namotion.Interceptor.Tracking.Change;

public class PropertyChangedObservable : IObservable<SubjectPropertyUpdate>, IWriteInterceptor
{
    private readonly Subject<SubjectPropertyUpdate> _subject = new();
    
    public object? WriteProperty(WritePropertyInterception context, Func<WritePropertyInterception, object?> next)
    {
        var currentValue = context.CurrentValue;
        var result = next(context); 

        var changedContext = new SubjectPropertyUpdate(context.Property, currentValue, result);
        _subject.OnNext(changedContext);
     
        return result;
    }

    public IDisposable Subscribe(IObserver<SubjectPropertyUpdate> observer)
    {
        return _subject.Subscribe(observer);
    }
    
    public void AttachTo(IInterceptorSubject subject)
    {
    }

    public void DetachFrom(IInterceptorSubject subject)
    {
    }
}
