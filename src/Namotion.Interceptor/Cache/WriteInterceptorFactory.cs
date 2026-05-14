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
                    context.Property.SetWriteTimestamp(context.WriteTimestampForStorage);
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
                    context.Property.SetWriteTimestamp(context.WriteTimestampForStorage);
                }
                return context.NewValue;
            }
        );
        return chain.Execute;
    }
}