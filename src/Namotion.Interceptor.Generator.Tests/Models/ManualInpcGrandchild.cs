using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Generator.Tests.Models;

// Third level of a chain rooted at a manual IRaisePropertyChanged base.
[InterceptorSubject]
public partial class ManualInpcGrandchild : ManualInpcSubject
{
    public partial string? GrandchildName { get; set; }
}
