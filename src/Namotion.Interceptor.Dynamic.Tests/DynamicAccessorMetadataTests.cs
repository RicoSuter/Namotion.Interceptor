using Namotion.Interceptor.Registry;

namespace Namotion.Interceptor.Dynamic.Tests;

public interface IReadOnlyGauge
{
    int Level { get; }
}

public interface IWriteOnlyValve
{
    int Target { set; }
}

/// <summary>
/// A dynamic subject must report the accessors its properties actually have, because consumers such as
/// the OPC UA server decide read-only exposure from HasSetter.
/// </summary>
public class DynamicAccessorMetadataTests
{
    [Fact]
    public void WhenPropertyHasNoSetter_ThenMetadataHasNoSetValue()
    {
        // Arrange & Act
        var subject = DynamicSubjectFactory.CreateDynamicSubject(typeof(IReadOnlyGauge));

        // Assert
        var metadata = subject.Properties["Level"];
        Assert.NotNull(metadata.GetValue);
        Assert.Null(metadata.SetValue);
    }

    [Fact]
    public void WhenPropertyHasNoGetter_ThenMetadataHasNoGetValue()
    {
        // Arrange & Act
        var subject = DynamicSubjectFactory.CreateDynamicSubject(typeof(IWriteOnlyValve));

        // Assert
        var metadata = subject.Properties["Target"];
        Assert.Null(metadata.GetValue);
        Assert.NotNull(metadata.SetValue);
    }

    [Fact]
    public void WhenWritingToReadOnlyProperty_ThenTheWriteIsIgnored()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var subject = DynamicSubjectFactory.CreateDynamicSubject(typeof(IReadOnlyGauge));
        subject.Context.AddFallbackContext(context);

        var property = subject.TryGetRegisteredSubject()!.TryGetProperty("Level")!;

        // Act
        property.SetValue(42);

        // Assert
        Assert.False(property.HasSetter);
        Assert.Equal(0, ((IReadOnlyGauge)subject).Level);
    }
}
