using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tracking.Tests.Models;

[InterceptorSubject]
public partial class EvaluatorRoot
{
    public partial EvaluatorMachine? Machine { get; set; }

    public partial EvaluatorConfiguration? Spare { get; set; }

    public partial int Anchor { get; set; }
}

/// <summary>
/// Declares the worker ahead of the configuration, so that the worker attaches first.
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
}
