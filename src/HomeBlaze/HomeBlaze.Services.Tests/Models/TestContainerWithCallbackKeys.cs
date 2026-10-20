using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Services.Tests.Models;

/// <summary>
/// Test model with a subject dictionary whose keys run a callback when a path formats them.
/// </summary>
[InterceptorSubject]
public partial class TestContainerWithCallbackKeys
{
    public partial Dictionary<CallbackKey, TestContainer> Items { get; set; }

    public TestContainerWithCallbackKeys()
    {
        Items = new Dictionary<CallbackKey, TestContainer>();
    }
}

/// <summary>
/// A dictionary key that runs <see cref="OnFormat"/> once, the next time it is formatted, so that a test can change
/// the graph while a path to an entry is being built.
/// </summary>
public sealed class CallbackKey(string name)
{
    public Action? OnFormat { get; set; }

    public override string ToString()
    {
        var onFormat = OnFormat;
        OnFormat = null;
        onFormat?.Invoke();
        return name;
    }
}
