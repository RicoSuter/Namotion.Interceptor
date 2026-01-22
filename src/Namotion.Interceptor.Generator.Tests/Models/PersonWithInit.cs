using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Generator.Tests.Models;

[InterceptorSubject]
public partial class PersonWithInit
{
    public partial string? Id { get; init; }

    // Track hook calls for testing
    public List<string> HookCalls { get; } = new();

    partial void OnIdChanging(ref string? newValue, ref bool cancel)
    {
        HookCalls.Add($"Changing:{newValue}");
    }

    partial void OnIdChanged(string? newValue)
    {
        HookCalls.Add($"Changed:{newValue}");
    }
}
