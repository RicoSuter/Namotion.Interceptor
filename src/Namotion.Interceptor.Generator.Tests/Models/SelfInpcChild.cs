using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Generator.Tests.Models;

// Child of a generated subject that implements IRaisePropertyChanged manually itself (not via a
// manual base class). The parent owns no wrapped raise, so the child must wrap at its call sites.
[InterceptorSubject]
public partial class SelfInpcChild : SelfInpcSubject
{
    public partial string? ChildName { get; set; }
}
