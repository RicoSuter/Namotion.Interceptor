using System.ComponentModel;

namespace Namotion.Interceptor.Generator.Tests.Models;

// A base that exposes the raise only through the explicit interface method: there is no directly
// callable raiser, so the generator emits a protected forwarder in the generated child.
public abstract class ExplicitInpcBase : INotifyPropertyChanged, IRaisePropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    void IRaisePropertyChanged.RaisePropertyChanged(string propertyName) =>
        SubjectChangeContext.RaisePropertyChanged(PropertyChanged, this, propertyName);
}
