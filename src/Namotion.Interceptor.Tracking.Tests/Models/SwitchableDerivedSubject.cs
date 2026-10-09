using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tracking.Tests.Models;

[InterceptorSubject]
public sealed partial class SwitchableDerivedSubject : IDisposable
{
    private int _getterCallCount;
    private int _blockNextEvaluation;
    private int _throwAfterBlockedEvaluation;

    internal ManualResetEventSlim EvaluationEntered { get; } = new(false);
    internal ManualResetEventSlim ContinueEvaluation { get; } = new(false);
    internal int GetterCallCount => Volatile.Read(ref _getterCallCount);
    internal bool UseSecond { get; set; }

    public partial int First { get; set; }
    public partial int Second { get; set; }

    internal void BlockNextEvaluation(bool thenThrow = false)
    {
        Volatile.Write(ref _throwAfterBlockedEvaluation, thenThrow ? 1 : 0);
        Volatile.Write(ref _blockNextEvaluation, 1);
    }

    [Derived]
    public int Selected
    {
        get
        {
            Interlocked.Increment(ref _getterCallCount);
            if (Interlocked.Exchange(ref _blockNextEvaluation, 0) == 1)
            {
                EvaluationEntered.Set();
                if (!ContinueEvaluation.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException("The test did not release the derived getter.");
                }

                if (Interlocked.Exchange(ref _throwAfterBlockedEvaluation, 0) == 1)
                {
                    throw new InvalidOperationException("The test made the released derived getter throw.");
                }
            }

            return UseSecond ? Second : First;
        }
    }

    public void Dispose()
    {
        EvaluationEntered.Dispose();
        ContinueEvaluation.Dispose();
    }
}
