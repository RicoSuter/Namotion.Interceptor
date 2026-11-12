using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Tracking.Tests.Models;

[InterceptorSubject]
public partial class EvaluatorRoot
{
    public partial EvaluatorMachine? Machine { get; set; }

    public partial EvaluatorConfiguration? Spare { get; set; }

    public partial int Anchor { get; set; }

    public partial byte[]? Data { get; set; }
}

/// <summary>
/// The tests rely on the worker attaching before the configuration.
/// </summary>
[InterceptorSubject]
public partial class EvaluatorMachine
{
    public EvaluatorMachine()
    {
        Worker = new EvaluatorWorker();
        Configuration = new EvaluatorConfiguration();
    }

    public partial EvaluatorWorker Worker { get; set; }

    public partial EvaluatorConfiguration Configuration { get; set; }
}

[InterceptorSubject]
public partial class EvaluatorConfiguration
{
    public partial int Value { get; set; }
}

/// <summary>
/// Evaluates its derived property with a delegate, so that a test decides what the getter reads and does.
/// </summary>
[InterceptorSubject]
public partial class EvaluatorWorker
{
    public Func<EvaluatorWorker, int>? Evaluator { get; set; }

    public int EvaluationCount { get; set; }

    [Derived]
    public int Result => Evaluator?.Invoke(this) ?? 0;

    public Func<EvaluatorWorker, object?>? ObjectEvaluator { get; set; }

    [Derived]
    public object? ObjectResult => ObjectEvaluator?.Invoke(this);

    /// <summary>
    /// An evaluator whose value changes, and that gains a dependency, once the configuration is attached: before that
    /// the configuration has no parent, and its value is read without being tracked.
    /// </summary>
    public static int ReadConfigurationOnceAttached(EvaluatorWorker worker)
    {
        var configuration = worker.TryGetFirstParent<EvaluatorMachine>()!.Configuration;
        return (configuration.TryGetFirstParent<EvaluatorMachine>() is not null ? 1 : 0) + configuration.Value;
    }
}
