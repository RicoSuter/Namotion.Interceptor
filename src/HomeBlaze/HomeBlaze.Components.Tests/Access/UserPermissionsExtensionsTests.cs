using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Authorization.Abstractions;
using HomeBlaze.Components.Tests.Models;
using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Xunit;

namespace HomeBlaze.Components.Tests.Access;

public class UserPermissionsExtensionsTests
{
    private static readonly IServiceProvider ServiceProvider = Mock.Of<IServiceProvider>();

    [Fact]
    public async Task WhenTheUserMayInvoke_ThenTheMethodRunsAndItsResultIsReturned()
    {
        // Arrange
        object?[]? received = null;
        var member = OperationTestGraph.CreateMember(new MethodMetadata(arguments =>
        {
            received = arguments;
            return 42;
        })
        {
            Parameters = [new MethodParameter { Name = "speed", Type = typeof(int) }]
        });

        // Act
        var result = await new TestUserPermissions().TryInvokeAsync(member, [5], ServiceProvider, CancellationToken.None);

        // Assert
        Assert.Equal(PermittedOperationOutcome.Completed, result.Outcome);
        Assert.Equal(42, result.Result);
        Assert.Null(result.Exception);
        Assert.Equal([5], received);
    }

    [Fact]
    public async Task WhenTheUserMayNotInvoke_ThenTheMethodDoesNotRun()
    {
        // Arrange
        var invocationCount = 0;
        var member = OperationTestGraph.CreateMember(new MethodMetadata(_ =>
        {
            invocationCount++;
            return null;
        }));
        var permissions = new TestUserPermissions { CannotInvoke = [member] };

        // Act
        var result = await permissions.TryInvokeAsync(member, null, ServiceProvider, CancellationToken.None);

        // Assert
        Assert.Equal(PermittedOperationOutcome.Denied, result.Outcome);
        Assert.Equal(0, invocationCount);
    }

    [Fact]
    public async Task WhenNoPermissionsAreConfigured_ThenTheMethodDoesNotRun()
    {
        // Arrange
        var invocationCount = 0;
        var member = OperationTestGraph.CreateMember(new MethodMetadata(_ =>
        {
            invocationCount++;
            return null;
        }));

        // Act
        var result = await DenyAllPermissions.Instance.TryInvokeAsync(member, null, ServiceProvider, CancellationToken.None);

        // Assert
        Assert.Equal(PermittedOperationOutcome.Denied, result.Outcome);
        Assert.Equal(0, invocationCount);
    }

    [Fact]
    public async Task WhenTheMethodThrows_ThenTheFailureIsReturned()
    {
        // Arrange
        var exception = new InvalidOperationException("Not connected.");
        var member = OperationTestGraph.CreateMember(new MethodMetadata(_ => Task.FromException(exception)));

        // Act
        var result = await new TestUserPermissions().TryInvokeAsync(member, null, ServiceProvider, CancellationToken.None);

        // Assert
        Assert.Equal(PermittedOperationOutcome.Failed, result.Outcome);
        Assert.Same(exception, result.Exception);
    }

    [Fact]
    public async Task WhenTheMemberHoldsNoMethod_ThenNothingRunsAndItIsDenied()
    {
        // Arrange
        var subject = new PersistedDevice(InterceptorSubjectContext.Create().WithRegistry());
        var member = subject.TryGetRegisteredSubject()!.AddProperty("Value", typeof(int), _ => 1, null);

        // Act
        var result = await new TestUserPermissions().TryInvokeAsync(member, null, ServiceProvider, CancellationToken.None);

        // Assert
        Assert.Equal(PermittedOperationOutcome.Denied, result.Outcome);
    }

    [Fact]
    public async Task WhenAMethodIsNamedByItsCSharpName_ThenTheMemberWithoutTheAsyncSuffixRuns()
    {
        // Arrange
        var invocationCount = 0;
        var member = OperationTestGraph.CreateMember(new MethodMetadata(_ =>
        {
            invocationCount++;
            return null;
        }));
        var permissions = new TestUserPermissions();

        // Act
        var canInvoke = permissions.CanInvoke(member.Subject, "RunAsync");
        var result = await permissions.TryInvokeAsync(member.Subject, "RunAsync", null, ServiceProvider, CancellationToken.None);

        // Assert
        Assert.True(canInvoke);
        Assert.Equal(PermittedOperationOutcome.Completed, result.Outcome);
        Assert.Equal(1, invocationCount);
    }

    [Fact]
    public async Task WhenTheSubjectHasNoSuchMethod_ThenItCannotBeInvokedAndIsDenied()
    {
        // Arrange
        var member = OperationTestGraph.CreateMember(new MethodMetadata(_ => null));
        var permissions = new TestUserPermissions();

        // Act
        var canInvoke = permissions.CanInvoke(member.Subject, "MissingAsync");
        var result = await permissions.TryInvokeAsync(member.Subject, "MissingAsync", null, ServiceProvider, CancellationToken.None);

        // Assert
        Assert.False(canInvoke);
        Assert.Equal(PermittedOperationOutcome.Denied, result.Outcome);
    }

    [Fact]
    public void WhenTheUserMayNotInvokeTheNamedMethod_ThenItCannotBeInvoked()
    {
        // Arrange
        var member = OperationTestGraph.CreateMember(new MethodMetadata(_ => null));
        var permissions = new TestUserPermissions { CannotInvoke = [member] };

        // Act
        var canInvoke = permissions.CanInvoke(member.Subject, "Run");

        // Assert
        Assert.False(canInvoke);
    }

    [Fact]
    public void WhenTheSubjectIsNotRegistered_ThenNoAdministrationIsAllowed()
    {
        // Arrange
        var device = new AdministeredDevice();
        var permissions = new TestUserPermissions();

        // Act
        var canConfigure = permissions.CanAdministerConfiguration(device);
        var canDelete = permissions.CanAdministerHoldingMembers(device);
        var canAdministerMember = permissions.CanAdminister(device, nameof(AdministeredDevice.Host));

        // Assert
        Assert.False(canConfigure);
        Assert.False(canDelete);
        Assert.False(canAdministerMember);
    }

    [Fact]
    public void WhenTheUserMayNotAdministerOneConfigurationMember_ThenTheSubjectCannotBeConfigured()
    {
        // Arrange
        var (_, device) = CreateStore();
        var permissions = new TestUserPermissions { CannotAdminister = [GetMember(device, nameof(AdministeredDevice.Label))] };

        // Act
        var canConfigure = permissions.CanAdministerConfiguration(device);

        // Assert
        Assert.False(canConfigure);
    }

    [Fact]
    public void WhenTheUserMayAdministerEveryConfigurationMember_ThenTheSubjectCanBeConfigured()
    {
        // Arrange
        var (_, device) = CreateStore();

        // Act
        var canConfigure = new TestUserPermissions().CanAdministerConfiguration(device);

        // Assert
        Assert.True(canConfigure);
    }

    [Fact]
    public void WhenTheSubjectHasNoParent_ThenItCannotBeDeleted()
    {
        // Arrange
        var (store, _) = CreateStore();

        // Act
        var canDelete = new TestUserPermissions().CanAdministerHoldingMembers(store);

        // Assert
        Assert.False(canDelete);
    }

    [Fact]
    public void WhenTheUserMayNotAdministerTheHoldingMember_ThenTheSubjectCannotBeDeleted()
    {
        // Arrange
        var (store, device) = CreateStore();
        var permissions = new TestUserPermissions { CannotAdminister = [GetMember(store, nameof(AdministeredStore.Device))] };

        // Act
        var canDelete = permissions.CanAdministerHoldingMembers(device);

        // Assert
        Assert.False(canDelete);
    }

    [Fact]
    public void WhenTheUserMayAdministerTheHoldingMember_ThenTheSubjectCanBeDeleted()
    {
        // Arrange
        var (_, device) = CreateStore();

        // Act
        var canDelete = new TestUserPermissions().CanAdministerHoldingMembers(device);

        // Assert
        Assert.True(canDelete);
    }

    [Fact]
    public void WhenTheSubjectHasNoSuchMember_ThenItCannotBeAdministered()
    {
        // Arrange
        var (_, device) = CreateStore();
        var permissions = new TestUserPermissions();

        // Act
        var canAdministerMissing = permissions.CanAdminister(device, "Missing");
        var canAdministerHost = permissions.CanAdminister(device, nameof(AdministeredDevice.Host));

        // Assert
        Assert.False(canAdministerMissing);
        Assert.True(canAdministerHost);
    }

    [Fact]
    public void WhenTheUserMayNotRunTheDeleteOperation_ThenTheSubjectCannotBeDeleted()
    {
        // Arrange
        var (_, device) = CreateStore();
        var deleteMember = AddDeleteOperation(device);
        var permissions = new TestUserPermissions { CannotInvoke = [deleteMember] };

        // Act
        var canDelete = permissions.CanDelete(device, deleteMember);

        // Assert
        Assert.False(canDelete);
    }

    [Fact]
    public void WhenTheUserMayNotAdministerTheHoldingMember_ThenTheSubjectCannotBeDeletedThroughItsDeleteOperation()
    {
        // Arrange
        var (store, device) = CreateStore();
        var deleteMember = AddDeleteOperation(device);
        var permissions = new TestUserPermissions { CannotAdminister = [GetMember(store, nameof(AdministeredStore.Device))] };

        // Act
        var canDelete = permissions.CanDelete(device, deleteMember);

        // Assert
        Assert.True(permissions.CanInvoke(deleteMember));
        Assert.False(canDelete);
    }

    [Fact]
    public void WhenTheUserMayRunTheDeleteOperationAndAdministerTheHoldingMember_ThenTheSubjectCanBeDeleted()
    {
        // Arrange
        var (_, device) = CreateStore();
        var deleteMember = AddDeleteOperation(device);

        // Act
        var canDelete = new TestUserPermissions().CanDelete(device, deleteMember);

        // Assert
        Assert.True(canDelete);
    }

    private static RegisteredSubjectProperty AddDeleteOperation(IInterceptorSubject subject)
    {
        var operation = new MethodMetadata(_ => null)
        {
            Kind = MethodKind.Operation,
            Purpose = OperationPurpose.Delete,
            PropertyName = "Delete",
            Title = "Delete",
            RequiresAdministration = true
        };
        return subject.TryGetRegisteredSubject()!.AddProperty("Delete", typeof(MethodMetadata), _ => operation, null);
    }

    private static (AdministeredStore Store, AdministeredDevice Device) CreateStore()
    {
        var device = new AdministeredDevice { Host = "host", Label = "Pump" };
        var store = new AdministeredStore(SubjectContextFactory.Create(new ServiceCollection())) { Device = device };
        return (store, device);
    }

    private static RegisteredSubjectProperty GetMember(IInterceptorSubject subject, string name) =>
        subject.TryGetRegisteredSubject()!.TryGetProperty(name)!;
}
