using System.Reactive.Subjects;

namespace Namotion.Interceptor.Tracking.Change;

public class PropertyChangedObservable : IObservable<SubjectPropertyChange>, IWriteInterceptor
{
    private readonly ISubject<SubjectPropertyChange> _subject = Subject.Synchronize(new Subject<SubjectPropertyChange>());

    public void WriteProperty<TProperty>(WritePropertyInterception<TProperty> context, Action<WritePropertyInterception<TProperty>> next)
    {
        next(context);

        var changedContext = new SubjectPropertyChange(
            context.Property, 
            SubjectMutationContext.GetCurrentSource(),
            SubjectMutationContext.GetCurrentTimestamp(),
            context.CurrentValue, context.NewValue);

        _subject.OnNext(changedContext);
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