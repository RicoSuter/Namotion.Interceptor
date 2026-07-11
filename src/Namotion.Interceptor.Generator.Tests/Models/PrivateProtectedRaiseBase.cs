using System.ComponentModel;

namespace Namotion.Interceptor.Generator.Tests.Models;

// A base whose callable raiser is private protected: visible to the generated child (same
// assembly), so the generator must not emit a forwarder (it would hide this method, CS0108).
public abstract class PrivateProtectedRaiseBase : INotifyPropertyChanged, IRaisePropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private protected void RaisePropertyChanged(string propertyName) =>
        SubjectChangeContext.RaisePropertyChanged(PropertyChanged, this, propertyName);

    void IRaisePropertyChanged.RaisePropertyChanged(string propertyName) => RaisePropertyChanged(propertyName);
}
