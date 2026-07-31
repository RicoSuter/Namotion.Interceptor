using System.Collections.Immutable;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Cache;

internal static class WriteInterceptorFactory<TProperty>
{
    public static WriteAction<TProperty> Create(ImmutableArray<IWriteInterceptor> interceptors)
    {
        if (interceptors.Length == 0)
        {
            return static (ref context, innerWriteValue) =>
            {
                lock (context.Property.Subject.SyncRoot)
                {
                    innerWriteValue(context.Property.Subject, context.NewValue);
                    context.IsWritten = true;
                    // Plain increment, no Interlocked: the enclosing lock is the subject's SyncRoot and the
                    // executor belongs to that subject, so the increment is exclusive. The contract used to
                    // be asserted in a helper that any caller could reach; inlined here the lock is lexically
                    // enclosing, which the compiler guarantees, so an assert would restate the line above it.
                    context.Revision = ++context.Executor.Revision;
                    context.FinalizeOrigin();
                    var raw = context.WriteTimestampRaw;
                    context.Property.SetWriteTimestamp(raw > 0 ? raw : 0);
                }
            };
        }

        var chain = new WriteInterceptorChain<TProperty>(
            interceptors,
            static (ref context, innerWriteValue) =>
            {
                lock (context.Property.Subject.SyncRoot)
                {
                    innerWriteValue(context.Property.Subject, context.NewValue);
                    context.IsWritten = true;
                    // See the zero-interceptor terminal above for why the increment needs no Interlocked
                    // and no lock assert.
                    context.Revision = ++context.Executor.Revision;
                    context.FinalizeOrigin();
                    var raw = context.WriteTimestampRaw;
                    context.Property.SetWriteTimestamp(raw > 0 ? raw : 0);
                }
                return context.NewValue;
            }
        );
        return chain.Execute;
    }
}