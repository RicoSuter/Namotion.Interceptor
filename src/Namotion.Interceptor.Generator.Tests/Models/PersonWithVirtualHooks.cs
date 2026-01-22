using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Generator.Tests.Models;

[InterceptorSubject]
public partial class PersonWithVirtualHooks
{
    public virtual partial string? Name { get; set; }

    // Track hook calls for testing
    public List<string> HookCalls { get; } = new();

    partial void OnNameChanging(ref string? newValue, ref bool cancel)
    {
        HookCalls.Add($"Base.Changing:{newValue}");
    }

    partial void OnNameChanged(string? newValue)
    {
        HookCalls.Add($"Base.Changed:{newValue}");
    }
}

[InterceptorSubject]
public partial class EmployeeWithVirtualHooks : PersonWithVirtualHooks
{
    public override partial string? Name { get; set; }

    public partial string? Department { get; set; }

    // Track hook calls for the override property
    public List<string> OverrideHookCalls { get; } = new();

    partial void OnNameChanging(ref string? newValue, ref bool cancel)
    {
        OverrideHookCalls.Add($"Derived.Changing:{newValue}");
    }

    partial void OnNameChanged(string? newValue)
    {
        OverrideHookCalls.Add($"Derived.Changed:{newValue}");
    }

    partial void OnDepartmentChanging(ref string? newValue, ref bool cancel)
    {
        OverrideHookCalls.Add($"Department.Changing:{newValue}");
    }

    partial void OnDepartmentChanged(string? newValue)
    {
        OverrideHookCalls.Add($"Department.Changed:{newValue}");
    }
}
