using System.Collections.Immutable;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Cache;

internal static class WriteInterceptorFactory<TProperty>
{
    public static WriteAction<TProperty> Create(ImmutableArray<IWriteInterceptor> interceptors)
    {
        if (interceptors.Length == 0)
        {
            return static (ref interception, innerWriteValue) =>
            {
                lock (interception.Property.Subject.SyncRoot)
                {
                    innerWriteValue(interception.Property.Subject, interception.NewValue);
                    interception.IsWritten = true;
                    var ticks = SubjectChangeContext.Current.ChangedTimestampUtcTicks;
                    interception.Property.SetWriteTimestampUtcTicks(ticks);
                    interception.WriteTimestampUtcTicks = ticks;
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
                    var ticks = SubjectChangeContext.Current.ChangedTimestampUtcTicks;
                    context.Property.SetWriteTimestampUtcTicks(ticks);
                    context.WriteTimestampUtcTicks = ticks;
                }
                return context.NewValue;
            }
        );
        return chain.Execute;
    }
}