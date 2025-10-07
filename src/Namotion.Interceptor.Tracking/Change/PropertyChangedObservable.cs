using System.Reactive.Subjects;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking.Change;

public class PropertyChangedObservable : IObservable<SubjectPropertyChange>, IWriteInterceptor
{
    private readonly ISubject<SubjectPropertyChange> _subject = Subject.Synchronize(new Subject<SubjectPropertyChange>());

    public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
        var oldValue = context.CurrentValue;
        
        next(ref context);

        var newValue = context.GetFinalValue();
        var changedContext = SubjectPropertyChange.Create(
            context.Property, 
            SubjectMutationContext.GetCurrentSource(),
            SubjectMutationContext.GetCurrentTimestamp(),
            oldValue, 
            newValue);
        
        _subject.OnNext(changedContext);
    }

    public IDisposable Subscribe(IObserver<SubjectPropertyChange> observer)
    {
        return _subject.Subscribe(observer);
    }
}