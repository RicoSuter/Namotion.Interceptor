using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

// Counts getter evaluations, which a concurrent write in another test can raise through the stabilization loop.
[Collection(DerivedPropertyWriteGenerationCollection.Name)]
public class DerivedPropertyAttachEvaluationTests
{
    [Fact]
    public void WhenSingleSubjectIsAttached_ThenDerivedPropertyIsEvaluatedOnce()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new SiblingReadingRoot(context);
        var machine = new SiblingReadingMachine();
        root.Machine = machine;
        var device = new SiblingReadingDevice();

        // Act
        machine.Device = device;

        // Assert
        Assert.Equal(1, device.EvaluationCount);
    }
}
