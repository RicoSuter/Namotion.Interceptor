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
            Assert.True(PendingOrigin.TryConsume(property, out var origin, out var sentValue));
            Assert.Equal(ChangeOriginKind.FromSource, origin.Kind);
            Assert.Same(source, origin.Source);
            Assert.Equal("sent", sentValue);
        }
    }

    [Fact]
    public void WhenConsumedTwice_ThenSecondConsumeReturnsLocal()
    {
        // Arrange
        var property = CreateProperty();
        using (PendingOrigin.Set(property, ChangeOrigin.FromSource(new object()), null))
        {
            PendingOrigin.TryConsume(property, out _, out _);

            // Act
            var consumed = PendingOrigin.TryConsume(property, out var origin, out _);

            // Assert
            Assert.False(consumed);
            Assert.Equal(ChangeOriginKind.Local, origin.Kind);
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
            var mismatch = PendingOrigin.TryConsume(otherProperty, out var mismatchOrigin, out _);
            var match = PendingOrigin.TryConsume(armedProperty, out var matchOrigin, out _);

            // Assert
            Assert.False(mismatch);
            Assert.Equal(ChangeOriginKind.Local, mismatchOrigin.Kind);
            Assert.True(match);
            Assert.Equal(ChangeOriginKind.FromSource, matchOrigin.Kind);
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
        var consumed = PendingOrigin.TryConsume(property, out var origin, out _);

        // Assert
        Assert.False(consumed);
        Assert.Equal(ChangeOriginKind.Local, origin.Kind);
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
                PendingOrigin.TryConsume(innerProperty, out _, out _);
            }

            // Act: after the inner scope disposes, the outer stamp must be intact.
            var consumed = PendingOrigin.TryConsume(outerProperty, out var origin, out var sentValue);

            // Assert
            Assert.True(consumed);
            Assert.Same(outerSource, origin.Source);
            Assert.Equal("outer", sentValue);
        }
    }
}
