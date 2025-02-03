using System.Reactive.Linq;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Sources;

public static class ObservableExtensions
{
    public static IObservable<IEnumerable<PropertyChangedContext>> BufferChanges(this IObservable<PropertyChangedContext> observable, TimeSpan bufferTime)
    {
        return observable
            .Buffer(bufferTime)
            .Where(propertyChanges => propertyChanges.Any())
            .Select(propertyChanges => propertyChanges.Reverse().DistinctBy(c => c.Property));
    }
}
