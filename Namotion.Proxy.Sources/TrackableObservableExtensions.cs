using Namotion.Proxy.Abstractions;
using System.Reactive.Linq;

namespace Namotion.Trackable.Sources;

public static class TrackableObservableExtensions
{
    public static IObservable<IEnumerable<ProxyPropertyChanged>> BufferChanges(this IObservable<ProxyPropertyChanged> observable, TimeSpan bufferTime)
    {
        return observable
            .Buffer(bufferTime)
            .Where(propertyChanges => propertyChanges.Any())
            .Select(propertyChanges => propertyChanges.Reverse().DistinctBy(c => (c.Proxy, c.PropertyName)));
    }
}
