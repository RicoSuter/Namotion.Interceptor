using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

[Collection(DerivedPropertyTraceCollection.Name)]
public class DerivedPropertyStabilizationWarningTests
{
    [Fact]
    public async Task WhenDependenciesStabilizeAfterAConcurrentWrite_ThenNoExhaustionWarningIsEmitted()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        using var subject = new SwitchableDerivedSubject(context);
        var unrelatedSubject = new Person(context);
        var callsBefore = subject.GetterCallCount;
        subject.UseSecond = true;
        subject.BlockNextEvaluation();
        using var traceCapture = new TraceCapture();

        // Act
        var trigger = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() => { subject.First = 1; });
        try
        {
            Assert.True(subject.EvaluationEntered.Wait(TimeSpan.FromSeconds(10)), "The derived getter did not start.");
            unrelatedSubject.FirstName = "Changed";
        }
        finally
        {
            subject.ContinueEvaluation.Set();
            await trigger.WaitAsync(TimeSpan.FromSeconds(10));
        }

        // Assert: only the evaluation the write starts. A changed dependency set (First to Second) is no
        // reason to evaluate again, and the unrelated write was not recorded, so it adds no evaluation.
        Assert.Equal(callsBefore + 1, subject.GetterCallCount);
        Assert.DoesNotContain("MaxStabilizationIterations", traceCapture.Text);
    }

    [Fact]
    public async Task WhenAnotherThreadWritesASourceDuringEveryEvaluationOfTheOwner_ThenNoExhaustionWarningIsEmitted()
    {
        // Arrange: each evaluation of the owner waits until the test thread wrote the source once, so the owner
        // evaluates again for another thread's write more often than the iteration limit.
        const int writes = 150;
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var root = new EvaluatorRoot(context);
        var machine = new EvaluatorMachine();
        var ownerThreadId = 0;
        var remainingWrites = writes;
        using var evaluationEntered = new AutoResetEvent(false);
        using var continueEvaluation = new AutoResetEvent(false);
        machine.Worker.Evaluator = _ =>
        {
            var value = root.Anchor;
            if (Environment.CurrentManagedThreadId == Volatile.Read(ref ownerThreadId) && Volatile.Read(ref remainingWrites) > 0)
            {
                evaluationEntered.Set();
                if (!continueEvaluation.WaitOne(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException("The test did not release the derived getter.");
                }
            }

            return value;
        };
        root.Machine = machine;
        using var traceCapture = new TraceCapture();

        // Act
        var owner = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() =>
        {
            Volatile.Write(ref ownerThreadId, Environment.CurrentManagedThreadId);
            root.Anchor = 1;
        });
        for (var write = 1; write <= writes; write++)
        {
            Assert.True(evaluationEntered.WaitOne(TimeSpan.FromSeconds(10)), "The owner did not evaluate.");
            root.Anchor = 1 + write;
            Interlocked.Decrement(ref remainingWrites);
            continueEvaluation.Set();
        }

        await owner.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        Assert.Equal(1 + writes, machine.Worker.Result);
        Assert.DoesNotContain("MaxStabilizationIterations", traceCapture.Text);
    }

    [Fact]
    public async Task WhenAnotherThreadWritesOnceDuringAGetterThatWritesWhatItReads_ThenTheOwnerStillStopsAtTheLimit()
    {
        // Arrange: the getter writes what it reads on the owner's thread; another thread writes the source once while
        // the first evaluation runs. A cap ends the getter's writes in case the limit does not.
        const int cap = 5000;
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var root = new EvaluatorRoot(context);
        var machine = new EvaluatorMachine();
        var ownerThreadId = 0;
        var evaluations = 0;
        using var evaluationEntered = new ManualResetEventSlim(false);
        using var continueEvaluation = new ManualResetEventSlim(false);
        machine.Worker.Evaluator = _ =>
        {
            var value = root.Anchor;
            if (Environment.CurrentManagedThreadId == Volatile.Read(ref ownerThreadId))
            {
                var evaluation = Interlocked.Increment(ref evaluations);
                if (evaluation == 1)
                {
                    evaluationEntered.Set();
                    if (!continueEvaluation.Wait(TimeSpan.FromSeconds(10)))
                    {
                        throw new TimeoutException("The test did not release the derived getter.");
                    }
                }

                if (evaluation < cap)
                {
                    root.Anchor = value + 1;
                }
            }

            return value;
        };
        root.Machine = machine;
        using var traceCapture = new TraceCapture();

        // Act
        var owner = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() =>
        {
            Volatile.Write(ref ownerThreadId, Environment.CurrentManagedThreadId);
            root.Anchor = 1;
        });
        try
        {
            Assert.True(evaluationEntered.Wait(TimeSpan.FromSeconds(10)), "The owner did not evaluate.");
            root.Anchor = 100;
        }
        finally
        {
            continueEvaluation.Set();
            await owner.WaitAsync(TimeSpan.FromSeconds(10));
        }

        // Assert
        Assert.Contains("MaxStabilizationIterations", traceCapture.Text);
        Assert.True(Volatile.Read(ref evaluations) < cap, $"The owner evaluated {evaluations} times.");
    }
}
