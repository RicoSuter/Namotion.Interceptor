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
