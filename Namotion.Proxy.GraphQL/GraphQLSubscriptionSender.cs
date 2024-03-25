using HotChocolate.Subscriptions;
using Microsoft.Extensions.Hosting;
using Namotion.Proxy.ChangeTracking;
using System.Reactive.Linq;

namespace Namotion.Proxy.GraphQL
{
    public class GraphQLSubscriptionSender<TProxy> : BackgroundService
        where TProxy : class
    {
        private readonly IProxyContext _context;
        private readonly TProxy _proxy;
        private readonly ITopicEventSender _sender;

        // TODO: Inject IProxyContext<TProxy> so that multiple contexts are supported.
        public GraphQLSubscriptionSender(IProxyContext context, TProxy proxy, ITopicEventSender sender)
        {
            _context = context;
            _proxy = proxy;
            _sender = sender;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await _context
                .GetHandler<IProxyPropertyChangedHandler>()
                .ForEachAsync(async (change) =>
                {
                    await _sender.SendAsync(nameof(Subscription<TProxy>.Root), _proxy, stoppingToken);
                }, stoppingToken);
        }
    }
}