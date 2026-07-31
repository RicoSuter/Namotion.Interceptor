using System.Collections.Immutable;
using System.Diagnostics;
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
                // Hoisted: innerWriteValue is opaque to the JIT, so a second read of the property
                // reference could not be folded into the first (PropertyReference advises this).
                var property = context.Property;
                var subject = property.Subject;
                lock (subject.SyncRoot)
                {
                    innerWriteValue(subject, context.NewValue);
                    context.IsWritten = true;
                    // Plain increment, no Interlocked: the enclosing lock is the subject's SyncRoot and the
                    // executor belongs to that subject, so the increment is exclusive. The lock half is
                    // lexically enclosing and therefore compiler-guaranteed; the executor-owns-subject half
                    // is only a construction-site convention, so it is asserted. A mismatched executor would
                    // increment another subject's counter under the wrong lock, silently and undetectably.
                    Debug.Assert(ReferenceEquals(context.Executor.Subject, subject),
                        "The context's executor must own the subject being locked: the plain increment relies on that pairing.");
                    context.Revision = ++context.Executor.Revision;
                    context.FinalizeOrigin();
                    var raw = context.WriteTimestampRaw;
                    property.SetWriteTimestamp(raw > 0 ? raw : 0);
                }
            };
        }

        var chain = new WriteInterceptorChain<TProperty>(
            interceptors,
            static (ref context, innerWriteValue) =>
            {
                var property = context.Property;
                var subject = property.Subject;
                lock (subject.SyncRoot)
                {
                    innerWriteValue(subject, context.NewValue);
                    context.IsWritten = true;
                    // See the zero-interceptor terminal above for why the property is hoisted, why the
                    // increment needs no Interlocked, and what the assert covers that the lock does not.
                    Debug.Assert(ReferenceEquals(context.Executor.Subject, subject),
                        "The context's executor must own the subject being locked: the plain increment relies on that pairing.");
                    context.Revision = ++context.Executor.Revision;
                    context.FinalizeOrigin();
                    var raw = context.WriteTimestampRaw;
                    property.SetWriteTimestamp(raw > 0 ? raw : 0);
                }
                return context.NewValue;
            }
        );
        return chain.Execute;
    }
}