using HomeBlaze.Abstractions;
using HomeBlaze.Services.Lifecycle;
using HomeBlaze.Services.Tests.Attributes;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Services.Tests;

public class SubjectPathResolverSecretTests
{
    [Fact]
    public void WhenResolvingASecretConfigurationPropertyForDisplay_ThenNothingIsResolved()
    {
        // Arrange
        var (resolver, _) = CreateResolver();

        // Act
        var property = resolver.ResolveDisplayableProperty("/SecretKey", PathStyle.Canonical);
        var value = resolver.ResolveValue("/SecretKey", PathStyle.Canonical);

        // Assert
        Assert.Null(property);
        Assert.Null(value);
        Assert.NotNull(resolver.ResolveProperty("/SecretKey", PathStyle.Canonical));
    }

    [Fact]
    public void WhenResolvingANonSecretConfigurationPropertyForDisplay_ThenItsValueIsResolved()
    {
        // Arrange
        var (resolver, _) = CreateResolver();

        // Act
        var property = resolver.ResolveDisplayableProperty("/PublicConfig", PathStyle.Canonical);
        var value = resolver.ResolveValue("PublicConfig", PathStyle.Canonical);

        // Assert
        Assert.Equal("PublicConfig", property?.Name);
        Assert.Equal("visible", value);
    }

    [Fact]
    public void WhenResolvingAPropertyExcludedFromExpressionsForDisplay_ThenNothingIsResolved()
    {
        // Arrange
        var (resolver, root) = CreateResolver();
        root.RawSource = "Password=s3cr3t";

        // Act
        var property = resolver.ResolveDisplayableProperty("/RawSource", PathStyle.Canonical);
        var scopedValue = resolver.ResolveScopedValue("RawSource", PathStyle.Canonical, root);

        // Assert
        Assert.Null(property);
        Assert.Null(scopedValue);
        Assert.Null(resolver.ResolveValue("RawSource", PathStyle.Canonical));
        Assert.NotNull(resolver.ResolveProperty("/RawSource", PathStyle.Canonical));
    }

    [Fact]
    public void WhenResolvingAPlainOrADerivedPropertyForDisplay_ThenOnlyTheDerivedOneIsResolved()
    {
        // Arrange
        var (resolver, _) = CreateResolver();

        // Act
        var plain = resolver.ResolveDisplayableProperty("/PlainText", PathStyle.Canonical);
        var derived = resolver.ResolveValue("/DerivedText", PathStyle.Canonical);

        // Assert
        Assert.Null(plain);
        Assert.NotNull(resolver.ResolveProperty("/PlainText", PathStyle.Canonical));
        Assert.Equal("visible!", derived);
    }

    private static (SubjectPathResolver Resolver, SecretConfigTestSubject Root) CreateResolver()
    {
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithService<IPropertyLifecycleHandler>(
                () => new PropertyAttributeInitializer(),
                handler => handler is PropertyAttributeInitializer);
        var root = new SecretConfigTestSubject(context) { SecretKey = "s3cr3t", PublicConfig = "visible" };
        return (new SubjectPathResolver(() => root), root);
    }
}
