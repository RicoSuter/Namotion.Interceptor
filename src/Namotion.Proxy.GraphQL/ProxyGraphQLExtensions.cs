using HotChocolate.Execution.Configuration;

using Microsoft.Extensions.Hosting;
using Namotion.Interceptor;
using Namotion.Proxy.GraphQL;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class ProxyGraphQLExtensions
{
    public static void AddGraphQLProxy<TSubject>(this IRequestExecutorBuilder builder)
        where TSubject : IInterceptorSubject
    {
        builder
            .Services
            .AddSingleton<IHostedService, GraphQLSubscriptionSender<TSubject>>();

        builder
            .AddQueryType<Query<TSubject>>()
            .AddSubscriptionType<Subscription<TSubject>>();
    }
}