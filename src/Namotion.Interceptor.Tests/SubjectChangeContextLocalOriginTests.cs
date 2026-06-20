using Xunit;

namespace Namotion.Interceptor.Tests;

public class SubjectChangeContextLocalOriginTests
{
    [Fact]
    public void WhenWithLocalOriginEntered_ThenSourceIsNullAndTimestampsPreserved()
    {
        // Arrange
        var source = new object();
        var changed = new DateTimeOffset(2026, 6, 20, 9, 0, 0, TimeSpan.Zero);
        var received = new DateTimeOffset(2026, 6, 20, 10, 0, 0, TimeSpan.Zero);

        // Act & Assert
        using (SubjectChangeContext.WithState(source, changed: changed, received: received))
        {
            Assert.Same(source, SubjectChangeContext.Current.Source);

            using (SubjectChangeContext.WithLocalOrigin())
            {
                // Inside the local-origin scope the source is cleared...
                Assert.Null(SubjectChangeContext.Current.Source);
                // ...but the ambient changed and received timestamps are both preserved.
                Assert.Equal(changed.UtcTicks, SubjectChangeContext.Current.ResolveChangedTimestamp());
                Assert.Equal(received, SubjectChangeContext.Current.ReceivedTimestamp);
            }

            // After dispose the previous source is restored.
            Assert.Same(source, SubjectChangeContext.Current.Source);
        }

        Assert.Null(SubjectChangeContext.Current.Source);
    }
}
