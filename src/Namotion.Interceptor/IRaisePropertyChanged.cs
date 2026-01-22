namespace Namotion.Interceptor;

/// <summary>
/// Allows raising PropertyChanged notifications from external components.
/// Implemented explicitly by generated subjects that support INotifyPropertyChanged.
/// </summary>
public interface IRaisePropertyChanged
{
    /// <summary>
    /// Raises the PropertyChanged event for the specified property.
    /// </summary>
    /// <param name="propertyName">The name of the property that changed.</param>
    void RaisePropertyChanged(string propertyName);
}
