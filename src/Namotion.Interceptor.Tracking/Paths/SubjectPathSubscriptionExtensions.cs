using System;
using System.Linq.Expressions;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Tracking.Paths;

/// <summary>Entry points for subscribing to a decomposed path from a subject, for example <c>subject.SubscribeToPath(x => x.Child.Children[2].Name, callback)</c>.</summary>
public static class SubjectPathSubscriptionExtensions
{
    /// <summary>
    /// Subscribes to the value of <paramref name="path"/> from <paramref name="subject"/>. The returned
    /// handle exposes the path's current value via <see cref="SubjectPathSubscription{TValue}.Current"/>
    /// and must be disposed to release the subscription.
    /// </summary>
    public static SubjectPathSubscription<TValue> SubscribeToPath<TSubject, TValue>(
        this TSubject subject,
        Expression<Func<TSubject, TValue>> path,
        SubjectPathChangeCallback<TValue> callback)
        where TSubject : IInterceptorSubject
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(callback);

        return Subscribe(subject, path);
    }

    /// <summary>Observer overload of <see cref="SubscribeToPath{TSubject,TValue}(TSubject, Expression{Func{TSubject,TValue}}, SubjectPathChangeCallback{TValue})"/>.</summary>
    public static SubjectPathSubscription<TValue> SubscribeToPath<TSubject, TValue>(
        this TSubject subject,
        Expression<Func<TSubject, TValue>> path,
        ISubjectPathChangeObserver<TValue> observer)
        where TSubject : IInterceptorSubject
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(observer);

        return Subscribe(subject, path);
    }

    private static SubjectPathSubscription<TValue> Subscribe<TSubject, TValue>(
        TSubject subject,
        Expression<Func<TSubject, TValue>> path)
        where TSubject : IInterceptorSubject
    {
        // Installing a chain from inside an active, not-yet-committing transaction would bind it to
        // speculative staged subjects a rollback could strand; refuse rather than leak.
        if (SubjectTransaction.Current is { IsCommitting: false })
        {
            throw new InvalidOperationException(
                "SubscribeToPath cannot be called inside an active transaction; subscribe before starting the transaction or after it commits.");
        }

        var segments = PathExpressionDecomposer.Decompose(path);
        return new SubjectPathSubscription<TValue>(subject, segments);
    }
}
