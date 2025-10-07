using System.Reactive.Linq;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Sources;

public static class ObservableExtensions
{
    /// <summary>
    /// Buffers the property changes for a given time.
    /// </summary>
    /// <param name="observable">The observable.</param>
    /// <param name="bufferTime">The buffer time.</param>
    /// <returns>The buffered changes.</returns>
    public static IObservable<IEnumerable<SubjectPropertyChange>> BufferChanges(
        this IObservable<SubjectPropertyChange> observable, TimeSpan bufferTime)
    {
        return observable
            .Buffer(bufferTime)
            .Where(changes => changes.Count > 0)
            .Select(changes => changes
                .Reverse()
                .DistinctBy(c => c.Property));
    }
}
