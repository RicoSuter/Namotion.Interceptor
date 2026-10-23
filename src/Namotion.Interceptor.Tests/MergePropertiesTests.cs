namespace Namotion.Interceptor.Tests;

public class MergePropertiesTests
{
    [Fact]
    public void WhenMergingANewProperty_ThenTheResultHoldsExistingAndNewProperties()
    {
        // Arrange
        var car = new Car();
        var subject = (IInterceptorSubject)car;
        var color = CreateProperty("Color", _ => "red");

        // Act
        var merged = subject.MergeProperties([color]);

        // Assert
        Assert.Equal(subject.Properties.Count + 1, merged.Count);
        Assert.True(merged.ContainsKey("Speed"));
        Assert.Equal("red", merged["Color"].GetValue?.Invoke(car));
    }

    [Fact]
    public void WhenMergingANameTheSubjectAlreadyHas_ThenMergePropertiesThrows()
    {
        // Arrange
        var subject = (IInterceptorSubject)new Car();
        var duplicate = CreateProperty("Speed", _ => -1);

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() => subject.MergeProperties([duplicate]));
        Assert.Equal("The subject 'Car' already has a property named 'Speed'.", exception.Message);
    }

    [Fact]
    public void WhenABatchRepeatsAName_ThenMergePropertiesThrows()
    {
        // Arrange
        var subject = (IInterceptorSubject)new Car();
        var first = CreateProperty("Color", _ => "red");
        var second = CreateProperty("Color", _ => "blue");

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() => subject.MergeProperties([first, second]));
        Assert.Equal("The subject 'Car' already has a property named 'Color'.", exception.Message);
    }

    internal static SubjectPropertyMetadata CreateProperty(string name, Func<IInterceptorSubject, object?> getValue)
    {
        return new SubjectPropertyMetadata(name, typeof(object), [], getValue, null, isIntercepted: true, isDynamic: true);
    }
}
