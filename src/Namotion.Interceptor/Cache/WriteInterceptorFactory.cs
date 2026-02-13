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
                innerWriteValue(interception.Property.Subject, interception.NewValue);
                interception.IsWritten = true;
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
                    if (ticks == 0)
                    {
                        ticks = SubjectChangeContext.GetTimestampFunction().UtcTicks;
                    }

                    context.Property.SetWriteTimestampUtcTicks(ticks);
                }
                return context.NewValue;
            }
        );
        return chain.Execute;
    }
}