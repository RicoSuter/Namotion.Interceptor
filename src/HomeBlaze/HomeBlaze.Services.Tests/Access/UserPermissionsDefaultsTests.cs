using HomeBlaze.Authorization.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;

namespace HomeBlaze.Services.Tests.Access;

public class UserPermissionsDefaultsTests
{
    [Fact]
    public void WhenServicesAreRegisteredWithoutAuthentication_ThenTheUserSeesNothing()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddHomeBlazeServices();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        // Act
        var permissions = scope.ServiceProvider.GetRequiredService<IUserPermissions>();

        // Assert
        var subject = new MethodExtTestSubject(InterceptorSubjectContext.Create().WithRegistry()).TryGetRegisteredSubject()!;
        var member = subject.AddProperty("Value", typeof(int), _ => 1, null);
        Assert.IsType<DenyAllPermissions>(permissions);
        Assert.False(permissions.CanShow(subject));
        Assert.False(permissions.CanRead(member));
        Assert.False(permissions.CanWrite(member));
        Assert.False(permissions.CanAdminister(member));
    }
}
