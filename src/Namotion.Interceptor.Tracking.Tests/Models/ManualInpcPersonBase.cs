using System.ComponentModel;

namespace Namotion.Interceptor.Tracking.Tests.Models;

// A base class that manually implements INotifyPropertyChanged + IRaisePropertyChanged (NOT via
// [InterceptorSubject]). The raise does not clear the ambient source, so framework callers must
// provide the local-origin scope around it.
public abstract class ManualInpcPersonBase : INotifyPropertyChanged, IRaisePropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public void RaisePropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
