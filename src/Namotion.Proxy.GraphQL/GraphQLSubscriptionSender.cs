using HotChocolate.Subscriptions;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor;

namespace Namotion.Proxy.GraphQL
{
    public class GraphQLSubscriptionSender<TProxy> : BackgroundService
        where TProxy : IInterceptorSubject
    {
        private readonly TProxy _proxy;
        private readonly IServiceProvider _provider;
        private readonly ITopicEventSender _sender;

        public GraphQLSubscriptionSender(TProxy proxy, ITopicEventSender sender)
        {
            _provider = proxy.Interceptors as IServiceProvider ??
                throw new InvalidOperationException($"Context is not set on {nameof(TProxy)}.");

            _proxy = proxy;
            _sender = sender;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await foreach (var changes in _provider
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