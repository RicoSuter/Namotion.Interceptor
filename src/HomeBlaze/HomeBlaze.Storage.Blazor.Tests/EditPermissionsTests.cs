using HomeBlaze.Authorization.Abstractions;
using HomeBlaze.Components.Tests.Access;
using Namotion.Interceptor.Attributes;
using Xunit;

namespace HomeBlaze.Storage.Blazor.Tests;

public class EditPermissionsTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WhenASubjectWithoutConfigurationDecidesItsEditPermission_ThenItsDecisionApplies(bool canEdit)
    {
        // Arrange
        var subject = new SelfGatedSubject { CanEditAnswer = canEdit };

        // Act
        var result = new TestUserPermissions().CanEdit(subject);

        // Assert
        Assert.Equal(canEdit, result);
    }
}

/// <summary>A subject without configuration members whose editor decides who may open it.</summary>
[InterceptorSubject]
public partial class SelfGatedSubject : ISubjectEditPermission
{
    public bool CanEditAnswer { get; init; }

    bool ISubjectEditPermission.CanEdit(IUserPermissions permissions) => CanEditAnswer;
}
