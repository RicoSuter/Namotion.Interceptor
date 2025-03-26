using System.Reactive.Subjects;

namespace Namotion.Interceptor.Tracking.Change;

public class PropertyChangedObservable : IObservable<SubjectPropertyChange>, IWriteInterceptor
{
    private readonly Subject<SubjectPropertyChange> _subject = new();

    public object? WriteProperty(WritePropertyInterception context, Func<WritePropertyInterception, object?> next)
    {
        var currentValue = context.CurrentValue;
        var result = next(context);

        var changedContext = new SubjectPropertyChange(
            context.Property, 
            SubjectMutationContext.GetCurrentSource(),
            SubjectMutationContext.GetCurrentTimestamp(),
            currentValue, result);

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