using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Generator.Tests.Models;

// Child of a generated subject that implements IRaisePropertyChanged manually itself.
[InterceptorSubject]
public partial class SelfInpcChild : SelfInpcSubject
{
    public partial string? ChildName { get; set; }
}
