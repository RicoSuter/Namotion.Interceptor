using System.ComponentModel;
using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Generator.Tests.Models;

// A generated subject that declares INotifyPropertyChanged + IRaisePropertyChanged manually in its
// own base list. Its manual raise owns the local-origin contract.
[InterceptorSubject]
public partial class SelfInpcSubject : INotifyPropertyChanged, IRaisePropertyChanged
{
    public partial string? Name { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void RaisePropertyChanged(string propertyName) =>
        SubjectChangeContext.RaisePropertyChanged(PropertyChanged, this, propertyName);

    void IRaisePropertyChanged.RaisePropertyChanged(string propertyName) => RaisePropertyChanged(propertyName);
}
