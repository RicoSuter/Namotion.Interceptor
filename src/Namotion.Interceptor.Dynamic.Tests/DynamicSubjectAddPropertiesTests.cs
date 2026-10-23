namespace Namotion.Interceptor.Dynamic.Tests;

public class DynamicSubjectAddPropertiesTests
{
    [Fact]
    public void WhenAddingANewProperty_ThenAddPropertiesExposesIt()
    {
        // Arrange
        var subject = new DynamicSubject();

        // Act
        subject.AddProperties(CreateProperty("Speed", 100));

        // Assert
        var properties = ((IInterceptorSubject)subject).Properties;
        Assert.Equal(100, properties["Speed"].GetValue?.Invoke(subject));
    }

    [Fact]
    public void WhenAddingANameTheSubjectAlreadyHas_ThenAddPropertiesThrowsAndChangesNothing()
    {
        // Arrange
        var subject = new DynamicSubject();
        subject.AddProperties(CreateProperty("Speed", 100));
        var before = ((IInterceptorSubject)subject).Properties;

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() => subject.AddProperties(CreateProperty("Speed", 200)));
        Assert.Equal("The subject 'DynamicSubject' already has a property named 'Speed'.", exception.Message);
        Assert.Same(before, ((IInterceptorSubject)subject).Properties);
        Assert.Equal(100, before["Speed"].GetValue?.Invoke(subject));
    }

    [Fact]
    public void WhenABatchRepeatsAName_ThenAddPropertiesAddsNothing()
    {
        // Arrange
        var subject = new DynamicSubject();
        var before = ((IInterceptorSubject)subject).Properties;

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() =>
            subject.AddProperties(CreateProperty("Speed", 100), CreateProperty("Speed", 200)));
        Assert.Same(before, ((IInterceptorSubject)subject).Properties);
        Assert.False(((IInterceptorSubject)subject).Properties.ContainsKey("Speed"));
    }

    private static SubjectPropertyMetadata CreateProperty(string name, int value)
    {
        return new SubjectPropertyMetadata(name, typeof(int), [], _ => value, null, isIntercepted: true, isDynamic: true);
    }
}
