using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Generator.Tests.Models;

[InterceptorSubject]
public partial class PersonWithHooks
{
    public partial string? FirstName { get; set; }

    // Track hook calls for testing
    public List<string> HookCalls { get; } = new();
    public string? LastChangingValue { get; private set; }
    public string? LastChangedValue { get; private set; }
    public bool ShouldCancel { get; set; }
    public string? ValueToCoerce { get; set; }
    public bool ShouldThrowInChanging { get; set; }
    public bool ShouldThrowInChanged { get; set; }

    partial void OnFirstNameChanging(ref string? newValue, ref bool cancel)
    {
        HookCalls.Add($"Changing:{newValue}");
        LastChangingValue = newValue;

        if (ShouldThrowInChanging)
        {
            throw new InvalidOperationException("Exception in OnChanging");
        }

        if (ShouldCancel)
        {
            cancel = true;
            return;
        }

        if (ValueToCoerce != null)
        {
            newValue = ValueToCoerce;
        }
    }

    partial void OnFirstNameChanged(string? newValue)
    {
        HookCalls.Add($"Changed:{newValue}");
        LastChangedValue = newValue;

        if (ShouldThrowInChanged)
        {
            throw new InvalidOperationException("Exception in OnChanged");
        }
    }
}
