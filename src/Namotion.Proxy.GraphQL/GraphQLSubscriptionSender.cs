using HotChocolate.Subscriptions;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor;
using Namotion.Interceptor.Tracking.Abstractions;

namespace Namotion.Proxy.GraphQL
{
    public class GraphQLSubscriptionSender<TProxy> : BackgroundService
        where TProxy : IInterceptorSubject
    {
        private readonly TProxy _proxy;
        private readonly ITopicEventSender _sender;
        private readonly IObservable<PropertyChangedContext> _changedObservable;

        public GraphQLSubscriptionSender(TProxy proxy, ITopicEventSender sender, IObservable<PropertyChangedContext> changedObservable)
        {
            _proxy = proxy;
            _sender = sender;
            _changedObservable = changedObservable;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await foreach (var changes in _changedObservable
                .ToAsyncEnumerable()
                .WithCancellation(stoppingToken))
            {
                // TODO: Send only changed diff
                await _sender.SendAsync(nameof(Subscription<TProxy>.Root), _proxy, stoppingToken);
            }
        }
    }
}