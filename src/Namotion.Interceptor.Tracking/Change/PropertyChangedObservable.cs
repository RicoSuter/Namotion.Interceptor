using System.Reactive.Subjects;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking.Change;

public class PropertyChangedObservable : IObservable<SubjectPropertyChange>, IWriteInterceptor
{
    private readonly Subject<SubjectPropertyChange> _subject = new();
    private readonly ISubject<SubjectPropertyChange> _syncSubject;

    public PropertyChangedObservable()
    {
        _syncSubject = Subject.Synchronize(_subject);
    }

    public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
        // Fast-path: if nobody observes property changes, skip all tracking overhead
        if (!_subject.HasObservers)
        {
            next(ref context);
            return;
        }

        var oldValue = context.CurrentValue;
        
        next(ref context);

        var newValue = context.GetFinalValue();

        var changeContext = SubjectChangeContext.Current;
        var propertyChange = SubjectPropertyChange.Create(
            context.Property, 
            changeContext.Source,
            changeContext.ChangedTimestamp,
            changeContext.ReceivedTimestamp,
            oldValue, 
            newValue);
        
        _syncSubject.OnNext(propertyChange);
    }

    public IDisposable Subscribe(IObserver<SubjectPropertyChange> observer)
    {
        return _syncSubject.Subscribe(observer);
    }
}