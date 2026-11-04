using System.Threading.Channels;
using HotChocolate.Subscriptions;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.GraphQL
{
    public class GraphQLSubscriptionSender<TSubject> : BackgroundService
        where TSubject : IInterceptorSubject
    {
        private readonly TSubject _subject;
        private readonly ITopicEventSender _sender;

        public GraphQLSubscriptionSender(TSubject subject, ITopicEventSender sender)
        {
            _subject = subject;
            _sender = sender;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var changes = Channel.CreateUnbounded<SubjectPropertyChange>(
                new UnboundedChannelOptions { SingleReader = true });

            using var subscription = _subject
                .Context
                .GetPropertyChangeObservable()
                .Subscribe(
                    change => changes.Writer.TryWrite(change),
                    exception => changes.Writer.TryComplete(exception),
                    () => changes.Writer.TryComplete());

            await foreach (var _ in changes.Reader.ReadAllAsync(stoppingToken))
            {
                // TODO: Send only changes
                await _sender.SendAsync(nameof(Subscription<TSubject>.Root), _subject, stoppingToken);
            }
        }
    }
}
