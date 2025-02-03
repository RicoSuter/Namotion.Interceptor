using HotChocolate.Execution.Configuration;
using HotChocolate.Subscriptions;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor;
using Namotion.Interceptor.GraphQL;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class SubjectGraphQLExtensions
{
    public static IRequestExecutorBuilder AddSubjectGraphQL<TSubject>(this IRequestExecutorBuilder builder)
        where TSubject : IInterceptorSubject
    {
        return builder.AddSubjectGraphQL(sp => sp.GetRequiredService<TSubject>());
    }
    
    public static IRequestExecutorBuilder AddSubjectGraphQL<TSubject>(this IRequestExecutorBuilder builder, Func<IServiceProvider, TSubject> subjectSelector)
        where TSubject : IInterceptorSubject
    {
        builder
            .Services
            .AddSingleton<IHostedService>(sp => 
                new GraphQLSubscriptionSender<TSubject>(subjectSelector(sp), 
                sp.GetRequiredService<ITopicEventSender>()));

        builder
            .AddQueryType<Query<TSubject>>()
            .AddSubscriptionType<Subscription<TSubject>>();

        return builder;
    }
}