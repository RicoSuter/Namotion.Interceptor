using System.Diagnostics;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

[Collection(DerivedPropertyWriteGenerationCollection.Name)]
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

            // Assert
            Assert.Equal(callsBefore + 2, subject.GetterCallCount);
            Assert.DoesNotContain("during dependency stabilization", traceOutput.ToString());
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }
    }

    [Fact]
    public void WhenDependenciesNeverStabilize_ThenAnExhaustionWarningIsEmitted()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var subject = new UnstableDerivedSubject(context);
        var callsBefore = subject.GetterCallCount;
        subject.AlternateDependencies = true;
        using var traceOutput = new StringWriter();
        using var listener = new TextWriterTraceListener(traceOutput);
        Trace.Listeners.Add(listener);

        try
        {
            // Act
            subject.First = 1;

            // Assert
            Assert.InRange(subject.GetterCallCount - callsBefore, 3, 1000);
            Assert.Contains("during dependency stabilization", traceOutput.ToString());
            Assert.Contains(nameof(UnstableDerivedSubject), traceOutput.ToString());
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }
    }
}
