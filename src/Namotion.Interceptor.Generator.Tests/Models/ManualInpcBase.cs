using System.ComponentModel;

namespace Namotion.Interceptor.Generator.Tests.Models;

// A base class that manually implements INotifyPropertyChanged + IRaisePropertyChanged
// (NOT via [InterceptorSubject]). A generated subclass must wrap its interface-cast raise
// call site in a local-origin scope.
public abstract class ManualInpcBase : INotifyPropertyChanged, IRaisePropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public void RaisePropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
