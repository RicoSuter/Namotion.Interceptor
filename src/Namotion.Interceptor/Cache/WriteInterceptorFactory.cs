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
                    interception.Property.SetWriteTimestampUtcTicks(SubjectChangeContext.Current.ChangedTimestampUtcTicks);
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
                    context.Property.SetWriteTimestampUtcTicks(SubjectChangeContext.Current.ChangedTimestampUtcTicks);
                }
                return context.NewValue;
            }
        );
        return chain.Execute;
    }
}