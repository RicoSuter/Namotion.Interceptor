using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Services.Tests.Models;

/// <summary>
/// Test model with a derived property that resolves a path, which counts how often its getter runs and keeps its last
/// result. A read of <see cref="Resolved"/> runs the getter itself, so a test reads <see cref="LastResult"/> instead.
/// </summary>
[InterceptorSubject]
public partial class PathWatcher
{
    /// <summary>Read by the getter, so that a write evaluates it again.</summary>
    public partial int Trigger { get; set; }

    public Func<object?>? Resolve { get; init; }

    public int EvaluationCount { get; private set; }

    public object? LastResult { get; private set; }

    [Derived]
    public object? Resolved
    {
        get
        {
            _ = Trigger;
            EvaluationCount++;
            return LastResult = Resolve?.Invoke();
        }
    }
}
