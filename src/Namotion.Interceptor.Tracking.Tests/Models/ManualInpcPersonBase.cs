using System.ComponentModel;

namespace Namotion.Interceptor.Tracking.Tests.Models;

// A base class that manually implements INotifyPropertyChanged + IRaisePropertyChanged (NOT via
// [InterceptorSubject]). The manual raise owns the local-origin contract.
public abstract class ManualInpcPersonBase : INotifyPropertyChanged, IRaisePropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void RaisePropertyChanged(string propertyName) =>
        SubjectChangeContext.RaisePropertyChanged(PropertyChanged, this, propertyName);

    void IRaisePropertyChanged.RaisePropertyChanged(string propertyName) => RaisePropertyChanged(propertyName);
}
