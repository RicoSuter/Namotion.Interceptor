using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tracking.Tests.Models;

/// <summary>
/// Parent subject that holds a <see cref="SideEffectPerson"/> as a child.
/// Used to trigger lifecycle detach (lock(_attachedSubjects) → lock(data_Greeting))
/// concurrently with Greeting recalculation (lock(data_Greeting) → getter → lock(_attachedSubjects)).
/// </summary>
[InterceptorSubject]
public partial class SideEffectHolder
{
    public partial SideEffectPerson? Person { get; set; }
}
