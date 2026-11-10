using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Components.Tests.Access;
using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Xunit;

namespace HomeBlaze.Storage.Blazor.Tests;

public class EditPermissionsTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WhenASubjectHasNoConfigurationMemberAndNoEditOperation_ThenNoViewerMayEditIt(bool hasEditComponent)
    {
        // Arrange
        var subject = new UnconfiguredSubject(CreateContext());

        // Act
        var canEdit = new TestUserPermissions().CanEdit(subject, hasEditComponent);

        // Assert
        Assert.False(canEdit);
    }

    [Fact]
    public void WhenTheViewerMayNotAdministerOneConfigurationMember_ThenTheyMayNotEditTheSubject()
    {
        // Arrange
        var subject = new ConfiguredSubject(CreateContext());
        var permissions = new TestUserPermissions { CannotAdminister = [GetMember(subject, nameof(ConfiguredSubject.Label))] };

        // Act
        var canEdit = permissions.CanEdit(subject, hasEditComponent: false);

        // Assert
        Assert.False(canEdit);
    }

    [Fact]
    public void WhenTheViewerMayAdministerEveryConfigurationMember_ThenTheyMayEditTheSubject()
    {
        // Arrange
        var subject = new ConfiguredSubject(CreateContext());

        // Act
        var canEdit = new TestUserPermissions().CanEdit(subject, hasEditComponent: false);

        // Assert
        Assert.True(canEdit);
    }

    [Fact]
    public void WhenTheViewerMayInvokeTheEditOperationOfASubjectWithAnEditComponent_ThenTheyMayEditIt()
    {
        // Arrange
        var subject = new OperationEditedSubject(CreateContext());
        var permissions = new TestUserPermissions();
        var refusingPermissions = new TestUserPermissions { CannotAdminister = [GetMember(subject, "Save")] };

        // Act
        var canEdit = permissions.CanEdit(subject, hasEditComponent: true);
        var canEditWithoutEditComponent = permissions.CanEdit(subject, hasEditComponent: false);
        var canEditWithoutAdministeringTheOperation = refusingPermissions.CanEdit(subject, hasEditComponent: true);

        // Assert
        Assert.True(canEdit);
        Assert.False(canEditWithoutEditComponent);
        Assert.False(canEditWithoutAdministeringTheOperation);
    }

    [Fact]
    public void WhenASubjectWithConfigurationHasAnEditOperation_ThenTheViewerNeedsToAdministerItsConfiguration()
    {
        // Arrange
        var subject = new ConfiguredOperationEditedSubject(CreateContext());
        var permissions = new TestUserPermissions { CannotAdminister = [GetMember(subject, nameof(ConfiguredOperationEditedSubject.Label))] };

        // Act
        var canEdit = permissions.CanEdit(subject, hasEditComponent: true);
        var canEditAsAdministrator = new TestUserPermissions().CanEdit(subject, hasEditComponent: true);

        // Assert
        Assert.True(permissions.CanInvoke(GetMember(subject, "Save")));
        Assert.False(canEdit);
        Assert.True(canEditAsAdministrator);
    }

    private static RegisteredSubjectProperty GetMember(IInterceptorSubject subject, string name) =>
        subject.TryGetRegisteredSubject()!.TryGetProperty(name)!;

    private static IInterceptorSubjectContext CreateContext() => SubjectContextFactory.Create(new ServiceCollection());
}

/// <summary>A subject without configuration members.</summary>
[InterceptorSubject]
public partial class UnconfiguredSubject
{
    [State]
    public partial string? Status { get; set; }
}

/// <summary>A subject with two configuration members.</summary>
[InterceptorSubject]
public partial class ConfiguredSubject
{
    [Configuration]
    public partial string? Host { get; set; }

    [Configuration]
    public partial string? Label { get; set; }
}

/// <summary>A subject without configuration members whose edit component saves through its edit operation.</summary>
[InterceptorSubject]
public partial class OperationEditedSubject
{
    [State]
    public partial string? Label { get; set; }

    [Operation(Title = "Save", RequiresAdministration = true, Purpose = OperationPurpose.Edit)]
    public void Save(string label) => Label = label;
}

/// <summary>A subject with a configuration member and an edit operation that requires no administration.</summary>
[InterceptorSubject]
public partial class ConfiguredOperationEditedSubject
{
    [Configuration]
    public partial string? Label { get; set; }

    [Operation(Title = "Save", Purpose = OperationPurpose.Edit)]
    public void Save(string label) => Label = label;
}
