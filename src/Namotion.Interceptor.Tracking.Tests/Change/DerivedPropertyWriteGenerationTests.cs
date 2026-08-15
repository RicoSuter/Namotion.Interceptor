using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

[Collection(DerivedPropertyWriteGenerationCollection.Name)]
public class DerivedPropertyWriteGenerationTests
{
    [Fact]
    public async Task WhenAVetoedWriteOccursDuringDependencyDiscovery_ThenTheGetterIsNotReevaluated()
    {
        // Arrange
        var evaluationContext = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        using var subject = new SwitchableDerivedSubject(evaluationContext);
        var callsBefore = subject.GetterCallCount;
        subject.UseSecond = true;
        subject.BlockNextEvaluation();

        var vetoContext = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        vetoContext.AddService<IWriteInterceptor>(new VetoingWriteInterceptor());
        var vetoedSubject = new Person(vetoContext);

        // Act
        var trigger = Task.Run(() => subject.First = 1);
        try
        {
            Assert.True(
                subject.EvaluationEntered.Wait(TimeSpan.FromSeconds(10)),
                "derived getter did not start");
            vetoedSubject.FirstName = "vetoed";
        }
        finally
        {
            subject.ContinueEvaluation.Set();
        }

        await trigger.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        Assert.Equal(callsBefore + 1, subject.GetterCallCount);
        Assert.Null(vetoedSubject.FirstName);
    }
}
