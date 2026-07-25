using System.ComponentModel;

namespace Namotion.Interceptor.Tracking.Tests.Models;

// A base class that manually implements INotifyPropertyChanged + IRaisePropertyChanged (NOT via
// [InterceptorSubject]). The hand-written raise owns the local-origin contract: it does not consume
// the one-shot pending origin, so a derived recalculation raised through it publishes locally.
public abstract class ManualInpcPersonBase : INotifyPropertyChanged, IRaisePropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public void RaisePropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
