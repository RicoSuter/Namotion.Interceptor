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

        return Subscribe(subject, path, new DelegateObserver<TValue>(callback));
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

        return Subscribe(subject, path, observer);
    }

    private static SubjectPathSubscription<TValue> Subscribe<TSubject, TValue>(
        TSubject subject,
        Expression<Func<TSubject, TValue>> path,
        ISubjectPathChangeObserver<TValue> observer)
        where TSubject : IInterceptorSubject
    {
        // Installing a chain from inside an active, not-yet-committing transaction would resolve its
        // intermediates against the transaction's speculative staged subjects and install listeners on them;
        // a rollback would then strand those listeners on subjects that never became part of the committed
        // graph. Refuse rather than leak: subscribe before the transaction, or after it commits.
        if (SubjectTransaction.Current is { IsCommitting: false })
        {
            throw new InvalidOperationException(
                "SubscribeToPath cannot be called inside an active transaction: the listener chain would bind to " +
                "the transaction's speculative staged subjects, which a rollback would strand. Subscribe before " +
                "starting the transaction or after it commits.");
        }

        var segments = PathExpressionDecomposer.Decompose(path);
        return new SubjectPathSubscription<TValue>(subject, segments, observer);
    }

    /// <summary>Wraps a <see cref="SubjectPathChangeCallback{TValue}"/> so the callback and observer entry points share one code path.</summary>
    private sealed class DelegateObserver<TValue>(SubjectPathChangeCallback<TValue> callback) : ISubjectPathChangeObserver<TValue>
    {
        public void OnChange(in SubjectPathChange<TValue> change) => callback(in change);
    }
}
