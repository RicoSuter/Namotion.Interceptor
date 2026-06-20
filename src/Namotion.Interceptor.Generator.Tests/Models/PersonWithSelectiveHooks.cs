using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Generator.Tests.Models;

[InterceptorSubject]
public partial class PersonWithSelectiveHooks
{
    // Hooked: OnHookedChanged is implemented, so its setter wraps the call in a local-origin scope.
    public partial string? Hooked { get; set; }

    // NotHooked: no hook bodies implemented, so its setter keeps the bare (erased) calls.
    public partial string? NotHooked { get; set; }

    public object? HookedSourceInsideChanged { get; private set; }

    partial void OnHookedChanged(string? newValue)
    {
        // Capture the ambient source seen inside the implemented hook body.
        HookedSourceInsideChanged = SubjectChangeContext.Current.Source;
    }
}
