using System.ComponentModel;
using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Generator.Tests.Models;

// A generated subject that declares INotifyPropertyChanged + IRaisePropertyChanged manually in its
// own base list: generation sees inherited INPC and emits no wrapped RaisePropertyChanged, so
// generated subclasses must wrap their raise call sites themselves.
[InterceptorSubject]
public partial class SelfInpcSubject : INotifyPropertyChanged, IRaisePropertyChanged
{
    public partial string? Name { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void RaisePropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
