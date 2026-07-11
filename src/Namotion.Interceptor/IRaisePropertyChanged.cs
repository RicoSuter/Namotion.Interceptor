namespace Namotion.Interceptor;

/// <summary>
/// Allows raising PropertyChanged notifications from external components.
/// Implemented explicitly by generated subjects that support INotifyPropertyChanged.
/// </summary>
public interface IRaisePropertyChanged
{
    /// <summary>
    /// Raises the PropertyChanged event for the specified property. Implementations must invoke
    /// subscribed handlers under <see cref="SubjectChangeContext.WithLocalOrigin"/> so writes made
    /// by handlers are published as local origin, while preserving a no-subscriber fast path.
    /// Forward to <see cref="SubjectChangeContext.RaisePropertyChanged"/> to get the compliant
    /// behavior in one call.
    /// </summary>
    /// <param name="propertyName">The name of the property that changed.</param>
    void RaisePropertyChanged(string propertyName);
}
