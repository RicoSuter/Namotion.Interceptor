namespace Namotion.Interceptor.Tests;

public class ChangeOriginTests
{
    [Fact]
    public void WhenDefault_ThenKindIsLocalAndSourceIsNull()
    {
        // Arrange & Act
        var origin = default(ChangeOrigin);

        // Assert
        Assert.Equal(ChangeOriginKind.Local, origin.Kind);
        Assert.Null(origin.Source);
    }

    [Fact]
    public void WhenFromSource_ThenKindAndSourceAreSet()
    {
        // Arrange
        var source = new object();

        // Act
        var origin = ChangeOrigin.FromSource(source);

        // Assert
        Assert.Equal(ChangeOriginKind.FromSource, origin.Kind);
        Assert.Same(source, origin.Source);
    }

    [Fact]
    public void WhenConfirmed_ThenKindAndSourceAreSet()
    {
        // Arrange
        var source = new object();

        // Act
        var origin = ChangeOrigin.Confirmed(source);

        // Assert
        Assert.Equal(ChangeOriginKind.Confirmed, origin.Kind);
        Assert.Same(source, origin.Source);
    }

    [Fact]
    public void WhenFactoryReceivesNullSource_ThenThrows()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => ChangeOrigin.FromSource(null!));
        Assert.Throws<ArgumentNullException>(() => ChangeOrigin.Confirmed(null!));
    }
}
