using System.Reactive.Subjects;

namespace Namotion.Interceptor.Tracking.Change;

public class PropertyChangedObservable : IObservable<SubjectPropertyChange>, IWriteInterceptor
{
    private readonly Subject<SubjectPropertyChange> _subject = new();

    private static readonly AsyncLocal<DateTimeOffset?> CurrentTimestamp = new();
    
    public static void SetCurrentTimestamp(DateTimeOffset timestamp)
    {
        CurrentTimestamp.Value = timestamp;
    }
    
    public static void ResetCurrentTimestamp()
    {
        CurrentTimestamp.Value = null;
    }
    
    public object? WriteProperty(WritePropertyInterception context, Func<WritePropertyInterception, object?> next)
    {
        var currentValue = context.CurrentValue;
        var result = next(context); 

        var timestamp = CurrentTimestamp.Value ?? DateTimeOffset.Now;
        var changedContext = new SubjectPropertyChange(context.Property, timestamp, currentValue, result);
        _subject.OnNext(changedContext);
     
        return result;
    }

    public IDisposable Subscribe(IObserver<SubjectPropertyChange> observer)
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
