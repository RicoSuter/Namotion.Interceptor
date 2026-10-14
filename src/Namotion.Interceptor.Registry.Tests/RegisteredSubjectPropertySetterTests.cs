using Namotion.Interceptor.Registry.Tests.Models;

namespace Namotion.Interceptor.Registry.Tests;

public class RegisteredSubjectPropertySetterTests
{
    [Theory]
    [InlineData(nameof(SetterAccessSubject.PublicSetter), true, true)]
    [InlineData(nameof(SetterAccessSubject.InternalSetter), true, false)]
    [InlineData(nameof(SetterAccessSubject.NoSetter), false, false)]
    public void WhenPropertyIsDeclaredOnTheClass_ThenOnlyAPublicSetterCountsAsPublic(string propertyName, bool expectedHasSetter, bool expectedHasPublicSetter)
    {
        // Arrange
        var subject = new SetterAccessSubject(InterceptorSubjectContext.Create().WithRegistry());
        var property = subject.TryGetRegisteredSubject()!.TryGetProperty(propertyName)!;

        // Act
        var hasSetter = property.HasSetter;
        var hasPublicSetter = property.HasPublicSetter;

        // Assert
        Assert.Equal((expectedHasSetter, expectedHasPublicSetter), (hasSetter, hasPublicSetter));
    }

    [Fact]
    public void WhenDynamicPropertyIsAdded_ThenItsSetterCountsAsPublic()
    {
        // Arrange
        var subject = new SetterAccessSubject(InterceptorSubjectContext.Create().WithRegistry());
        var registered = subject.TryGetRegisteredSubject()!;
        string? value = null;
        var withSetter = registered.AddProperty<string>("WithSetter", _ => value, (_, newValue) => value = newValue);
        var withoutSetter = registered.AddProperty<string>("WithoutSetter", _ => value);

        // Act
        var withSetterIsPublic = withSetter.HasPublicSetter;
        var withoutSetterIsPublic = withoutSetter.HasPublicSetter;

        // Assert
        Assert.True(withSetterIsPublic);
        Assert.False(withoutSetterIsPublic);
    }
}
