using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking.Change;

public class PropertyChangeObservable : IObservable<SubjectPropertyChange>, IWriteInterceptor
{
    private readonly Subject<SubjectPropertyChange> _subject = new();
    private readonly ISubject<SubjectPropertyChange> _syncSubject;

    public PropertyChangeObservable()
    {
        _syncSubject = Subject.Synchronize(_subject);
    }

    public bool ShouldInterceptWrite
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _subject.HasObservers;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
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