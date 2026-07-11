using System.ComponentModel;

namespace Namotion.Interceptor.Generator.Tests.Models;

// A base class that manually implements INotifyPropertyChanged + IRaisePropertyChanged
// (NOT via [InterceptorSubject]). The manual raise owns the local-origin contract.
public abstract class ManualInpcBase : INotifyPropertyChanged, IRaisePropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void RaisePropertyChanged(string propertyName)
    {
        var handler = PropertyChanged;
        if (handler is null)
        {
            return;
        }

        using (SubjectChangeContext.WithLocalOrigin())
        {
            handler(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    void IRaisePropertyChanged.RaisePropertyChanged(string propertyName) => RaisePropertyChanged(propertyName);
}
