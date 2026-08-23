using System.Collections.Immutable;
using System.Diagnostics;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Cache;

/// <summary>
/// Builds the terminals for the structural write route. Each terminal duplicates the matching
/// scalar terminal in <see cref="WriteInterceptorFactory{TProperty}"/> body for body and only
/// wraps it in the attachment guard: the duplication is deliberate, because sharing the body
/// would restructure the scalar terminal, which must keep its exact shape (no added call or
/// branch on the hot path). A change to a scalar terminal must be mirrored here.
/// </summary>
internal static class StructuralWriteInterceptorFactory<TProperty>
{
    public static WriteAction<TProperty> Create(ImmutableArray<IWriteInterceptor> interceptors)
    {
        if (interceptors.Length == 0)
        {
            return static (ref context, innerWriteValue) =>
            {
                var property = context.Property;
                var subject = property.Subject;
                lock (subject.SyncRoot)
                {
                    // Lock order: the subject's SyncRoot first, then the executor's attachment
                    // monitor. The guard rejects the write when the attachment revision moved
                    // since entry, and stays held through the commit so a transition cannot land
                    // between the check and the write it validated. See the scalar terminal in
                    // WriteInterceptorFactory for why the property is hoisted, why the increment
                    // needs no Interlocked, what the assert covers, and why the origin kind is
                    // read before FinalizeOrigin.
                    var executor = context.Executor;
                    executor.EnterAttachmentGuard(context.AttachmentRevisionAtEntry);
                    try
                    {
                        innerWriteValue(subject, context.NewValue);
                        context.IsWritten = true;
                        Debug.Assert(ReferenceEquals(context.Executor.Subject, subject),
                            "The context's executor must own the subject being locked: the plain increment relies on that pairing.");
                        context.Revision = ++context.Executor.Revision;
                        var isFromSource = context.Origin.Kind == ChangeOriginKind.FromSource;

                        context.FinalizeOrigin();
                        var raw = context.WriteTimestampRaw;
                        property.SetWriteState(raw > 0 ? raw : 0, context.Revision, isFromSource);
                    }
                    finally
                    {
                        executor.ExitAttachmentGuard();
                    }
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
                    // See the zero-interceptor structural terminal above for the guard and lock
                    // order, and the scalar terminal in WriteInterceptorFactory for the rest.
                    var executor = context.Executor;
                    executor.EnterAttachmentGuard(context.AttachmentRevisionAtEntry);
                    try
                    {
                        innerWriteValue(subject, context.NewValue);
                        context.IsWritten = true;
                        Debug.Assert(ReferenceEquals(context.Executor.Subject, subject),
                            "The context's executor must own the subject being locked: the plain increment relies on that pairing.");
                        context.Revision = ++context.Executor.Revision;
                        var isFromSource = context.Origin.Kind == ChangeOriginKind.FromSource;

                        context.FinalizeOrigin();
                        var raw = context.WriteTimestampRaw;
                        property.SetWriteState(raw > 0 ? raw : 0, context.Revision, isFromSource);
                    }
                    finally
                    {
                        executor.ExitAttachmentGuard();
                    }
                }
                return context.NewValue;
            }
        );
        return chain.Execute;
    }
}
