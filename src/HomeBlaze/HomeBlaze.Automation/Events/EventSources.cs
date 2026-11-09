using System.Reflection;
using System.Runtime.CompilerServices;
using HomeBlaze.Abstractions.Messaging;

namespace HomeBlaze.Automation.Events;

/// <summary>
/// The events a subject publishes: the types <c>T</c> of the <see cref="IObservable{T}"/> interfaces of its type where
/// <c>T</c> implements <see cref="IEvent"/>, found once per type. A subscription goes through a cached delegate typed to
/// the event, so a subject that publishes several event types is subscribed to the requested one.
/// </summary>
internal static class EventSources
{
    private static readonly MethodInfo SubscribeTypedMethod =
        new Func<object, Action<IEvent>, IDisposable>(SubscribeTyped<IEvent>).Method.GetGenericMethodDefinition();

    // Weak, so that the types of an unloaded plugin are not kept alive.
    private static readonly ConditionalWeakTable<Type, Type[]> EventTypesBySubjectType = new();
    private static readonly ConditionalWeakTable<Type, Func<object, Action<IEvent>, IDisposable>> SubscribersByEventType = new();

    public static IReadOnlyList<Type> GetEventTypes(Type subjectType) =>
        EventTypesBySubjectType.GetValue(subjectType, static type => type
            .GetInterfaces()
            .Where(contract => contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IObservable<>))
            .Select(contract => contract.GetGenericArguments()[0])
            .Where(eventType => typeof(IEvent).IsAssignableFrom(eventType))
            .ToArray());

    /// <summary>Finds an event type of the subject type by its name or its full name, or null.</summary>
    public static Type? FindEventType(Type subjectType, string name) =>
        GetEventTypes(subjectType).FirstOrDefault(eventType => eventType.Name == name || eventType.FullName == name);

    /// <summary>Subscribes to the events of the type that the source publishes; the source must implement <c>IObservable</c> of it.</summary>
    public static IDisposable Subscribe(object source, Type eventType, Action<IEvent> onEvent)
    {
        var subscribe = SubscribersByEventType.GetValue(eventType, static type =>
            SubscribeTypedMethod.MakeGenericMethod(type).CreateDelegate<Func<object, Action<IEvent>, IDisposable>>());
        return subscribe(source, onEvent);
    }

    private static IDisposable SubscribeTyped<TEvent>(object source, Action<IEvent> onEvent)
        where TEvent : IEvent =>
        ((IObservable<TEvent>)source).Subscribe(new EventObserver<TEvent>(onEvent));

    private sealed class EventObserver<TEvent>(Action<IEvent> onEvent) : IObserver<TEvent>
        where TEvent : IEvent
    {
        public void OnNext(TEvent value) => onEvent(value);

        public void OnError(Exception error)
        {
            // A source that fails publishes no more events; the trigger waits until the graph gives it a new source.
        }

        public void OnCompleted()
        {
            // A completed source publishes no more events, the same as a failed one.
        }
    }
}
