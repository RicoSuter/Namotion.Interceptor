using System.Reactive.Concurrency;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

public class DerivedPropertyTypeGateTests
{
    [Fact]
    public void WhenNoPropertyOfTypeIsTracked_ThenGateStaysDownAndWriteCreatesNoData()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var withContext = new UntrackedGateSubject(context);
        var withoutContext = new UntrackedGateSubject();

        // Act
        withContext.Value = "a";
        withoutContext.Value = "a";

        // Assert
        Assert.False(DerivedPropertyTypeGate.Of(withContext).IsRaised);
        Assert.Null(new PropertyReference(withContext, nameof(UntrackedGateSubject.Value)).TryGetDerivedPropertyData());
        Assert.Null(new PropertyReference(withoutContext, nameof(UntrackedGateSubject.Value)).TryGetDerivedPropertyData());
    }

    [Fact]
    public void WhenPropertyOfTypeIsReadByDerivedProperty_ThenGateIsRaisedAndLaterWritesArePublished()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var source = new TrackedGateSource { Value = "a" };
        var isRaisedBeforeFirstRead = DerivedPropertyTypeGate.Of(source).IsRaised;
        var reader = new TrackedGateReader { Source = source };
        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Add);

        // Act
        var root = new TrackedGateRoot(context) { Reader = reader };
        source.Value = "b";

        // Assert
        Assert.False(isRaisedBeforeFirstRead);
        Assert.True(DerivedPropertyTypeGate.Of(source).IsRaised);
        var change = Assert.Single(changes, c => c.Property.Name == nameof(TrackedGateReader.SourceValue));
        Assert.Equal("b", change.GetNewValue<string?>());
        Assert.Same(reader, root.Reader);
    }
}
