using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tracking.Tests.Models;

/// <summary>
/// Test model whose property setter throws when a value is applied. Used to exercise the apply
/// failure path of <c>ApplyAllChanges</c>.
/// </summary>
[InterceptorSubject]
public partial class ThrowingSetterPerson
{
    public partial string? Name { get; set; }

    partial void OnNameChanging(ref string? newValue, ref bool cancel)
    {
        throw new InvalidOperationException("Setter failed.");
    }
}
