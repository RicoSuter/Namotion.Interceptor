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

public class ProtectedSetterDevice : DynamicSubject
{
    public virtual int Position { get; protected set; }

    public int Mode { get; protected set; }

    public virtual int Speed { get; set; }
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

    [Theory]
    [InlineData(nameof(ProtectedSetterDevice.Position), false)]
    [InlineData(nameof(ProtectedSetterDevice.Mode), false)]
    [InlineData(nameof(ProtectedSetterDevice.Speed), true)]
    public void WhenBaseClassPropertyHasASetter_ThenOnlyAPublicSetterCountsAsPublic(string propertyName, bool expectedHasPublicSetter)
    {
        // Arrange
        IInterceptorSubject subject = DynamicSubjectFactory.CreateSubject<ProtectedSetterDevice>();
        subject.Context.AddFallbackContext(InterceptorSubjectContext.Create().WithRegistry());
        var property = subject.TryGetRegisteredSubject()!.TryGetProperty(propertyName)!;

        // Act
        var hasSetter = property.HasSetter;
        var hasPublicSetter = property.HasPublicSetter;

        // Assert
        Assert.True(hasSetter);
        Assert.Equal(expectedHasPublicSetter, hasPublicSetter);
    }

    [Fact]
    public void WhenInterfacePropertyHasASetter_ThenItsSetterCountsAsPublic()
    {
        // Arrange
        var subject = DynamicSubjectFactory.CreateDynamicSubject(typeof(IMotor));
        subject.Context.AddFallbackContext(InterceptorSubjectContext.Create().WithRegistry());
        var property = subject.TryGetRegisteredSubject()!.TryGetProperty(nameof(IMotor.Speed))!;

        // Act
        var hasPublicSetter = property.HasPublicSetter;

        // Assert
        Assert.True(hasPublicSetter);
    }
}
