namespace Namotion.Interceptor.Tests;

public class PendingOriginTests
{
    private static PropertyReference CreateProperty(string name = "Name")
    {
        var subject = new Car(InterceptorSubjectContext.Create());
        return new PropertyReference(subject, name);
    }

    [Fact]
    public void WhenSetAndConsumedWithMatchingProperty_ThenOriginAndSentValueAreReturned()
    {
        // Arrange
        var property = CreateProperty();
        var source = new object();

        // Act & Assert
        using (PendingOrigin.Set(property, ChangeOrigin.FromSource(source), "sent"))
        {
            Assert.True(PendingOrigin.TryConsume(property, out var attempted));
            Assert.Equal(ChangeOriginKind.FromSource, attempted.Origin.Kind);
            Assert.Same(source, attempted.Origin.Source);
            Assert.Equal("sent", attempted.SentValue);
        }
    }

    [Fact]
    public void WhenConsumedTwice_ThenSecondConsumeReturnsLocal()
    {
        // Arrange
        var property = CreateProperty();
        using (PendingOrigin.Set(property, ChangeOrigin.FromSource(new object()), null))
        {
            PendingOrigin.TryConsume(property, out _);

            // Act
            var consumed = PendingOrigin.TryConsume(property, out var attempted);

            // Assert
            Assert.False(consumed);
            Assert.Equal(ChangeOriginKind.Local, attempted.Origin.Kind);
        }
    }

    [Fact]
    public void WhenTargetDoesNotMatch_ThenConsumeReturnsLocalAndSlotStaysSet()
    {
        // Arrange
        var armedProperty = CreateProperty("Name");
        var otherProperty = CreateProperty("OtherName");

        using (PendingOrigin.Set(armedProperty, ChangeOrigin.FromSource(new object()), null))
        {
            // Act
            var mismatch = PendingOrigin.TryConsume(otherProperty, out var mismatchAttempted);
            var match = PendingOrigin.TryConsume(armedProperty, out var matchAttempted);

            // Assert
            Assert.False(mismatch);
            Assert.Equal(ChangeOriginKind.Local, mismatchAttempted.Origin.Kind);
            Assert.True(match);
            Assert.Equal(ChangeOriginKind.FromSource, matchAttempted.Origin.Kind);
        }
    }

    [Fact]
    public void WhenScopeIsDisposedWithoutConsumption_ThenSlotIsCleared()
    {
        // Arrange
        var property = CreateProperty();
        using (PendingOrigin.Set(property, ChangeOrigin.FromSource(new object()), null))
        {
        }

        // Act
        var consumed = PendingOrigin.TryConsume(property, out var attempted);

        // Assert
        Assert.False(consumed);
        Assert.Equal(ChangeOriginKind.Local, attempted.Origin.Kind);
    }

    [Fact]
    public void WhenNestedSetScopeIsDisposed_ThenOuterStampIsRestored()
    {
        // Arrange
        var outerProperty = CreateProperty("Name");
        var innerProperty = CreateProperty("OtherName");
        var outerSource = new object();

        using (PendingOrigin.Set(outerProperty, ChangeOrigin.FromSource(outerSource), "outer"))
        {
            using (PendingOrigin.Set(innerProperty, ChangeOrigin.FromSource(new object()), "inner"))
            {
                PendingOrigin.TryConsume(innerProperty, out _);
            }

            // Act: after the inner scope disposes, the outer stamp must be intact.
            var consumed = PendingOrigin.TryConsume(outerProperty, out var attempted);

            // Assert
            Assert.True(consumed);
            Assert.Same(outerSource, attempted.Origin.Source);
            Assert.Equal("outer", attempted.SentValue);
        }
    }
}
