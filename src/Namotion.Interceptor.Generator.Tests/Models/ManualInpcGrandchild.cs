using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Generator.Tests.Models;

// Third level of a chain rooted at a manual IRaisePropertyChanged base: ManualInpcGrandchild :
// ManualInpcSubject : ManualInpcBase. No generated subject in the chain owns a wrapped
// RaisePropertyChanged, so every generated level must wrap the inherited (unwrapped) raise at its
// own call sites.
[InterceptorSubject]
public partial class ManualInpcGrandchild : ManualInpcSubject
{
    public partial string? GrandchildName { get; set; }
}
