using System.Diagnostics;
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

            // Assert: only the evaluation the write starts. A changed dependency set (First to Second) is no
            // reason to evaluate again, and the unrelated write was not recorded, so it adds no evaluation.
            Assert.Equal(callsBefore + 1, subject.GetterCallCount);
            listener.Flush();
            Assert.DoesNotContain("MaxStabilizationIterations", traceOutput.ToString());
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }
    }
}
