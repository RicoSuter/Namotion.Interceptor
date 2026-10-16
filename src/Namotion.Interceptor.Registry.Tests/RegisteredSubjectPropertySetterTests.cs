using Namotion.Interceptor.Registry.Tests.Models;

namespace Namotion.Interceptor.Registry.Tests;

public class RegisteredSubjectPropertySetterTests
{
    [Theory]
    [InlineData(nameof(SetterAccessSubject.PublicSetter), true, true)]
    [InlineData(nameof(SetterAccessSubject.InternalSetter), true, false)]
    [InlineData(nameof(SetterAccessSubject.ProtectedSetter), true, false)]
    [InlineData(nameof(SetterAccessSubject.ProtectedInternalSetter), true, false)]
    [InlineData(nameof(SetterAccessSubject.PrivateProtectedSetter), true, false)]
    [InlineData(nameof(SetterAccessSubject.PrivateSetter), true, false)]
    [InlineData(nameof(SetterAccessSubject.InitSetter), false, false)]
    [InlineData(nameof(SetterAccessSubject.NoSetter), false, false)]
    [InlineData(nameof(SetterAccessSubject.DerivedWithSetter), true, true)]
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
    public void WhenPropertyIsRequired_ThenItsPublicSetterCountsAsPublic()
    {
        // Arrange
        var subject = new RequiredSetterSubject(InterceptorSubjectContext.Create().WithRegistry()) { Required = "Value" };
        var property = subject.TryGetRegisteredSubject()!.TryGetProperty(nameof(RequiredSetterSubject.Required))!;

        // Act
        var hasPublicSetter = property.HasPublicSetter;

        // Assert
        Assert.True(hasPublicSetter);
    }

    [Fact]
    public void WhenAnOverrideDeclaresOnlyTheGetter_ThenTheInheritedProtectedSetterIsNotPublic()
    {
        // Arrange
        var subject = new GetterOverrideSubject(InterceptorSubjectContext.Create().WithRegistry());
        var property = subject.TryGetRegisteredSubject()!.TryGetProperty(nameof(GetterOverrideSubject.Name))!;

        // Act
        var hasSetter = property.HasSetter;
        var hasPublicSetter = property.HasPublicSetter;

        // Assert
        Assert.True(hasSetter);
        Assert.False(hasPublicSetter);
    }

    [Fact]
    public void WhenPropertyIsAnExplicitInterfaceImplementation_ThenItsSetterCountsAsPublic()
    {
        // Arrange
        var subject = new ExplicitInterfaceSubject(InterceptorSubjectContext.Create().WithRegistry());
        var property = subject.TryGetRegisteredSubject()!.TryGetProperty(nameof(INamedSubject.Name))!;

        // Act
        var hasSetter = property.HasSetter;
        var hasPublicSetter = property.HasPublicSetter;

        // Assert
        Assert.True(hasSetter);
        Assert.True(hasPublicSetter);
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
