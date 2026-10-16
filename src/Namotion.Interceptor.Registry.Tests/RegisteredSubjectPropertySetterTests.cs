using System.Reflection;
using System.Reflection.Emit;
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
    public void WhenInitSetterIsMarkedWithAPolyfillOfIsExternalInit_ThenItIsNotPublic()
    {
        // Arrange
        var subject = new SetterAccessSubject();
        string? value = null;
        ((IInterceptorSubject)subject).AddProperties(new SubjectPropertyMetadata(
            CreateInitOnlyPropertyWithPolyfill(), _ => value, (_, newValue) => value = (string?)newValue, isIntercepted: false, isDynamic: true));
        ((IInterceptorSubject)subject).Context.AddFallbackContext(InterceptorSubjectContext.Create().WithRegistry());
        var property = subject.TryGetRegisteredSubject()!.TryGetProperty("Name")!;

        // Act
        var hasSetter = property.HasSetter;
        var hasPublicSetter = property.HasPublicSetter;

        // Assert
        Assert.True(hasSetter);
        Assert.False(hasPublicSetter);
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

    /// <summary>
    /// Emits a property whose public setter carries the init-only modifier of an own IsExternalInit type, as the
    /// compiler emits it for an assembly that targets a framework without that type.
    /// </summary>
    private static PropertyInfo CreateInitOnlyPropertyWithPolyfill()
    {
        var assemblyName = new AssemblyName("PolyfillInitAssembly");
        var module = AssemblyBuilder
            .DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run)
            .DefineDynamicModule(assemblyName.Name!);

        var polyfill = module
            .DefineType("System.Runtime.CompilerServices.IsExternalInit", TypeAttributes.NotPublic | TypeAttributes.Sealed | TypeAttributes.Abstract)
            .CreateType();

        var holder = module.DefineType("PolyfillInitHolder", TypeAttributes.Public | TypeAttributes.Abstract);
        var setter = holder.DefineMethod(
            "set_Name",
            MethodAttributes.Public | MethodAttributes.Abstract | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.SpecialName,
            CallingConventions.HasThis,
            typeof(void),
            [polyfill],
            null,
            [typeof(string)],
            null,
            null);
        holder.DefineProperty("Name", PropertyAttributes.None, typeof(string), null).SetSetMethod(setter);

        return holder.CreateType().GetProperty("Name")!;
    }
}
