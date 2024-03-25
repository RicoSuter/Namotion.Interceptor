using HotChocolate.Execution.Configuration;
using Namotion.Proxy.GraphQL;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class TrackableGraphQLExtensions
{
    public static void AddTrackedGraphQL<TTrackable>(this IRequestExecutorBuilder builder)
        where TTrackable : class
    {
        builder
            .Services
            .AddHostedService<GraphQLSubscriptionSender<TTrackable>>();

        builder
            .AddQueryType<Query<TTrackable>>()
            .AddSubscriptionType<Subscription<TTrackable>>();
    }
}