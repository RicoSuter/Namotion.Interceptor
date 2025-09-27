using System.Reactive.Subjects;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking.Change;

public class PropertyChangedObservable : IObservable<SubjectPropertyChange>, IWriteInterceptor
{
    private readonly ISubject<SubjectPropertyChange> _subject = Subject.Synchronize(new Subject<SubjectPropertyChange>());

    public void WriteProperty<TProperty>(ref WritePropertyContext<TProperty> context, WriteInterceptionAction<TProperty> next)
    {
        next(ref context);

        var changedContext = new SubjectPropertyChange(
            context.Property, 
            SubjectMutationContext.GetCurrentSource(),
            SubjectMutationContext.GetCurrentTimestamp(),
            context.CurrentValue, context.GetCurrentValue());
        
        _subject.OnNext(changedContext);
    }

    public IDisposable Subscribe(IObserver<SubjectPropertyChange> observer)
    {
        return _subject.Subscribe(observer);
    }
}