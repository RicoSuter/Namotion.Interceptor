using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tracking.Tests.Models;

/// <summary>
/// Test model with a derived property getter that has side effects:
/// writing to a subject-typed property during evaluation.
/// Used to verify that RecalculateDerivedProperty evaluates the getter
/// outside lock(data), preventing deadlock with LifecycleInterceptor.
/// </summary>
[InterceptorSubject]
public partial class SideEffectPerson
{
    public partial string? Name { get; set; }

    public partial Person? Companion { get; set; }

    [Derived]
    public string Greeting => ComputeGreeting();

    private string ComputeGreeting()
    {
        // Side effect: writes to a subject-typed property during getter evaluation.
        // This triggers LifecycleInterceptor.WriteProperty → lock(_attachedSubjects).
        // Without the unlocked evaluation in RecalculateDerivedProperty, this would
        // deadlock when concurrent lifecycle operations acquire lock(data) for Greeting.
        Companion = null;
        return $"Hello, {Name}";
    }
}
