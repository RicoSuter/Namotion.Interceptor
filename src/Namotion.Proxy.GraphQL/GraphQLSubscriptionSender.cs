using HotChocolate.Subscriptions;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor;
using Namotion.Interceptor.Tracking;

namespace Namotion.Proxy.GraphQL
{
    public class GraphQLSubscriptionSender<TProxy> : BackgroundService
        where TProxy : IInterceptorSubject
    {
        private readonly TProxy _proxy;
        private readonly ITopicEventSender _sender;

        public GraphQLSubscriptionSender(TProxy proxy, ITopicEventSender sender)
        {
            _proxy = proxy;
            _sender = sender;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await foreach (var changes in _proxy
                .Context
                .GetPropertyChangedObservable()
                .ToAsyncEnumerable()
                .WithCancellation(stoppingToken))
            {
                // TODO: Send only changed diff
                await _sender.SendAsync(nameof(Subscription<TProxy>.Root), _proxy, stoppingToken);
            }
        }
    }
}