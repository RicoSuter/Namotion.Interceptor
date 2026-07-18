using System.Linq.Expressions;
using System.Reflection;

namespace Namotion.Interceptor.Tracking.Change;

public static class PropertyChangeSubscriptionExtensions
{
    /// <summary>
    /// Subscribes an observer to changes of a single property (subject instance + name). Delivery is
    /// synchronous, on the writing thread, and dormant while the subject is not attached to a context
    /// with a <see cref="PropertyChangeInterceptor"/>. See <see cref="IPropertyChangeObserver"/> for the contract.
    /// </summary>
    /// <remarks>
    /// Disposing the returned handle is mandatory: the subject holds a strong reference, so a dropped
    /// handle keeps the observer alive and permanently disables the process-wide idle write fast path.
    /// Under concurrent writes to the same property, notifications may arrive out of commit order because
    /// dispatch runs outside the subject lock; if you need the current value, re-read the property rather
    /// than relying on the delivered new value.
    /// A write that commits after this subscription is installed is always delivered; a write that committed
    /// before the install may not be, and reading the property after subscribing observes that earlier state;
    /// a write that raced the install may be delivered with OldValue equal to NewValue.
    /// </remarks>
    public static IDisposable Subscribe(this PropertyReference property, IPropertyChangeObserver observer)
    {
        var metadata = property.Metadata; // throws InvalidOperationException when the name is not a known property
        if (!(metadata.IsIntercepted || metadata.IsDerived))
        {
            throw new ArgumentException(
                $"Property '{property.Name}' on {property.Subject.GetType().Name} cannot be subscribed to: it is not an intercepted or derived property, so its changes never enter the interception chain.",
                nameof(property));
        }

        return PropertyChangeSubscription.Create(property, observer);
    }

    /// <summary>Delegate overload of <see cref="Subscribe(PropertyReference, IPropertyChangeObserver)"/>.</summary>
    public static IDisposable Subscribe(this PropertyReference property, PropertyChangeCallback callback)
    {
        return property.Subscribe(new DelegateObserver(callback));
    }

    /// <summary>
    /// Strongly-typed subscription to a direct property of <paramref name="subject"/>, for example
    /// <c>subject.SubscribeToProperty(x => x.Temperature, observer)</c>. Only a direct property access on
    /// the lambda parameter is accepted; chained, captured, static, field, and method selectors throw.
    /// </summary>
    /// <remarks>
    /// Disposing the returned handle is mandatory: the subject holds a strong reference, so a dropped
    /// handle keeps the observer alive and permanently disables the process-wide idle write fast path.
    /// Under concurrent writes to the same property, notifications may arrive out of commit order because
    /// dispatch runs outside the subject lock; if you need the current value, re-read the property rather
    /// than relying on the delivered new value.
    /// A write that commits after this subscription is installed is always delivered; a write that committed
    /// before the install may not be, and reading the property after subscribing observes that earlier state;
    /// a write that raced the install may be delivered with OldValue equal to NewValue.
    /// </remarks>
    public static IDisposable SubscribeToProperty<TSubject, TValue>(
        this TSubject subject,
        Expression<Func<TSubject, TValue>> propertySelector,
        IPropertyChangeObserver observer)
        where TSubject : IInterceptorSubject
    {
        var name = ResolveDirectPropertyName(propertySelector);
        return new PropertyReference(subject, name).Subscribe(observer);
    }

    /// <summary>Delegate overload of <see cref="SubscribeToProperty{TSubject,TValue}(TSubject, Expression{Func{TSubject,TValue}}, IPropertyChangeObserver)"/>.</summary>
    public static IDisposable SubscribeToProperty<TSubject, TValue>(
        this TSubject subject,
        Expression<Func<TSubject, TValue>> propertySelector,
        PropertyChangeCallback callback)
        where TSubject : IInterceptorSubject
    {
        return subject.SubscribeToProperty(propertySelector, new DelegateObserver(callback));
    }

    private static string ResolveDirectPropertyName<TSubject, TValue>(Expression<Func<TSubject, TValue>> propertySelector)
    {
        var body = propertySelector.Body;

        // Unwrap a Convert/ConvertChecked boxing or numeric-cast node (e.g. Expression<Func<T, object>>).
        while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
        {
            body = unary.Operand;
        }

        if (body is not MemberExpression member
            || member.Member is not PropertyInfo property
            || member.Expression != propertySelector.Parameters[0])
        {
            throw new ArgumentException(
                "Only a direct property access on the lambda parameter is supported, for example x => x.Foo. " +
                "Chained (x => x.Child.Foo), captured-variable, static, field, and method selectors are not allowed.",
                nameof(propertySelector));
        }

        return property.Name;
    }

    private sealed class DelegateObserver(PropertyChangeCallback callback) : IPropertyChangeObserver
    {
        public void OnChange(in SubjectPropertyChange change) => callback(in change);
    }
}
