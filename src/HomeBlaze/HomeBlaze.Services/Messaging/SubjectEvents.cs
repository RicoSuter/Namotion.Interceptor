using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HomeBlaze.Abstractions.Messaging;

namespace HomeBlaze.Services.Messaging;

/// <summary>
/// Finds the events a subject publishes and subscribes to the events of one type. A subject publishes the events of each
/// type <c>T</c> that implements <see cref="IEvent"/> and for which its type implements <see cref="IObservable{T}"/>.
/// The event types of a subject type and the typed subscription of an event type are found once and cached weakly, so
/// the cache keeps no type alive. All members are thread-safe.
/// </summary>
public static class SubjectEvents
{
    private static readonly MethodInfo SubscribeTypedMethod =
        new Func<object, Action<IEvent>, IDisposable>(SubscribeTyped<IEvent>).Method.GetGenericMethodDefinition();

    private static readonly ConditionalWeakTable<Type, Type[]> EventTypesBySubjectType = new();
    private static readonly ConditionalWeakTable<Type, Func<object, Action<IEvent>, IDisposable>> SubscribersByEventType = new();

    /// <summary>Gets the event types that subjects of the type publish, in no particular order; empty when it publishes none.</summary>
    /// <param name="subjectType">The type of the subject.</param>
    public static ImmutableArray<Type> GetEventTypes(Type subjectType) =>
        ImmutableCollectionsMarshal.AsImmutableArray(EventTypesBySubjectType.GetValue(subjectType, static type => type
            .GetInterfaces()
            .Where(contract => contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IObservable<>))
            .Select(contract => contract.GetGenericArguments()[0])
            .Where(eventType => typeof(IEvent).IsAssignableFrom(eventType))
            .ToArray()));

    /// <summary>Finds the event type that subjects of the type publish by its name or its full name.</summary>
    /// <param name="subjectType">The type of the subject.</param>
    /// <param name="name">The name or the full name of the event type, compared ordinally.</param>
    /// <returns>The event type, or null when subjects of the type publish none of that name.</returns>
    public static Type? FindEventType(Type subjectType, string name)
    {
        foreach (var eventType in GetEventTypes(subjectType))
        {
            if (eventType.Name == name || eventType.FullName == name)
            {
                return eventType;
            }
        }

        return null;
    }

    /// <summary>
    /// Subscribes to the events of one type that a source publishes, until the subscription is disposed. An error or the
    /// completion that the source reports is not passed on.
    /// </summary>
    /// <param name="source">The source, which implements <see cref="IObservable{T}"/> of <paramref name="eventType"/>.</param>
    /// <param name="eventType">The type of the events, which implements <see cref="IEvent"/>.</param>
    /// <param name="onEvent">Called with each event, on the thread that the source publishes it on.</param>
    /// <returns>The subscription.</returns>
    /// <exception cref="ArgumentException"><paramref name="eventType"/> does not implement <see cref="IEvent"/>.</exception>
    /// <exception cref="InvalidCastException"><paramref name="source"/> does not publish events of <paramref name="eventType"/>.</exception>
    /// <remarks>Under Native AOT, <paramref name="eventType"/> must be a reference type.</remarks>
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
            // A source that fails publishes no more events.
        }

        public void OnCompleted()
        {
            // A completed source publishes no more events, the same as a failed one.
        }
    }
}
