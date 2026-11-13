using System.Diagnostics;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

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
        using var traceOutput = new StringWriter();
        using var listener = new TextWriterTraceListener(traceOutput);
        Trace.Listeners.Add(listener);

        try
        {
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

            // Assert: the evaluation the write starts, and one more because the dependency set changed
            // from First to Second. The unrelated write was not recorded, so it adds no evaluation.
            Assert.Equal(callsBefore + 2, subject.GetterCallCount);
            Assert.DoesNotContain("during dependency stabilization", traceOutput.ToString());
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }
    }
}
