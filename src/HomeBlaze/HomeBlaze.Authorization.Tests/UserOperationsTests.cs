using HomeBlaze.Abstractions;
using HomeBlaze.Services;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Xunit;

namespace HomeBlaze.Authorization.Tests;

public sealed class UserOperationsTests
{
    private const string BuiltInReason = "Built-in users cannot be removed, reset, locked or unlocked.";
    private const string KeyReason = "Only the built-in user mcp has a key.";

    private readonly Authorization _authorization;

    public UserOperationsTests()
    {
        _authorization = new Authorization();
        _ = new TestRoot(TestContext.Create())
        {
            Children = new Dictionary<string, IInterceptorSubject> { ["Authorization"] = _authorization }
        };
        _authorization.ReplaceUsers(
        [
            new UserSnapshot("mom", null, [], false, false, null, IsBuiltIn: false, HasKey: false, DisplayName: "Mom M\u00fcller"),
            new UserSnapshot("dad", null, [], false, false, null, IsBuiltIn: false, HasKey: false),
            new UserSnapshot(NameRules.AnonymousUserName, null, [], false, false, null, IsBuiltIn: true, HasKey: false),
            new UserSnapshot(NameRules.McpUserName, null, [], false, false, null, IsBuiltIn: true, HasKey: false)
        ]);
    }

    public static TheoryData<string> AccountOperations => ["Remove", "ResetPassword", "Lock", "Unlock"];

    [Theory]
    [MemberData(nameof(AccountOperations))]
    public void WhenTheUserIsBuiltIn_ThenTheOperationIsDisabledWithTheReason(string operationName)
    {
        // Act
        var anonymous = GetOperation(NameRules.AnonymousUserName, operationName);
        var mcp = GetOperation(NameRules.McpUserName, operationName);

        // Assert
        Assert.False(anonymous.IsMethodEnabled());
        Assert.False(mcp.IsMethodEnabled());
        Assert.Equal(BuiltInReason, GetDisabledReason(anonymous));
        Assert.Equal(BuiltInReason, GetDisabledReason(mcp));
    }

    [Theory]
    [MemberData(nameof(AccountOperations))]
    public void WhenThePersonSignsIn_ThenTheOperationIsEnabledWithoutAReason(string operationName)
    {
        // Act
        var operation = GetOperation("mom", operationName);

        // Assert
        Assert.True(operation.IsMethodEnabled());
        Assert.Null(GetDisabledReason(operation));
    }

    [Theory]
    [InlineData("CreateKey")]
    [InlineData("RevokeKey")]
    public void WhenTheUserIsNotMcp_ThenTheKeyOperationIsDisabledWithTheReason(string operationName)
    {
        // Act
        var person = GetOperation("mom", operationName);
        var anonymous = GetOperation(NameRules.AnonymousUserName, operationName);
        var mcp = GetOperation(NameRules.McpUserName, operationName);

        // Assert
        Assert.False(person.IsMethodEnabled());
        Assert.False(anonymous.IsMethodEnabled());
        Assert.Equal(KeyReason, GetDisabledReason(person));
        Assert.Equal(KeyReason, GetDisabledReason(anonymous));
        Assert.True(mcp.IsMethodEnabled());
        Assert.Null(GetDisabledReason(mcp));
    }

    [Fact]
    public void WhenTheUserIsBuiltIn_ThenSetRolesStaysEnabled()
    {
        // Act
        var anonymous = GetOperation(NameRules.AnonymousUserName, "SetRoles");
        var person = GetOperation("mom", "SetRoles");

        // Assert
        Assert.True(anonymous.IsMethodEnabled());
        Assert.True(person.IsMethodEnabled());
    }

    [Fact]
    public void WhenRolesAreAssigned_ThenTheirParameterIsEnteredWithTheRoleNamesEditor()
    {
        // Act
        var setRoles = GetOperation("mom", "SetRoles").TryGetMethodMetadata()!;
        var addUser = _authorization.TryGetRegisteredSubject()!.TryGetProperty("AddUser")!.TryGetMethodMetadata()!;

        // Assert
        Assert.Equal(AuthorizationParameterEditors.RoleNames, setRoles.Parameters.Single(parameter => parameter.Name == "roles").Editor);
        Assert.Equal(AuthorizationParameterEditors.RoleNames, addUser.Parameters.Single(parameter => parameter.Name == "roles").Editor);
        Assert.All(addUser.Parameters.Where(parameter => parameter.Name != "roles"), parameter => Assert.Null(parameter.Editor));
    }

    [Fact]
    public void WhenTheRolesOrTheDisplayNameOfAUserAreSet_ThenTheirDialogStartsWithTheCurrentValue()
    {
        // Act
        var setRoles = GetOperation("mom", "SetRoles").TryGetMethodMetadata()!;
        var setDisplayName = GetOperation("mom", "SetDisplayName").TryGetMethodMetadata()!;
        var addUser = _authorization.TryGetRegisteredSubject()!.TryGetProperty("AddUser")!.TryGetMethodMetadata()!;

        // Assert
        Assert.Equal(nameof(User.Roles), setRoles.Parameters.Single(parameter => parameter.Name == "roles").InitialValueProperty);
        Assert.Equal(nameof(User.DisplayName), setDisplayName.Parameters.Single(parameter => parameter.Name == "displayName").InitialValueProperty);
        Assert.All(addUser.Parameters, parameter => Assert.Null(parameter.InitialValueProperty));
    }

    [Fact]
    public void WhenAUserHasADisplayName_ThenItIsShownAndTheTitleNamesBothNames()
    {
        // Act
        var mom = _authorization.Users["mom"];
        var dad = _authorization.Users["dad"];

        // Assert
        Assert.Equal("Mom M\u00fcller", mom.DisplayName);
        Assert.Equal("Mom M\u00fcller (mom)", ((ITitleProvider)mom).Title);
        Assert.Null(dad.DisplayName);
        Assert.Equal("dad", ((ITitleProvider)dad).Title);
    }

    [Fact]
    public void WhenTheUserIsBuiltIn_ThenSetDisplayNameIsDisabledWithTheReason()
    {
        // Act
        var anonymous = GetOperation(NameRules.AnonymousUserName, "SetDisplayName");
        var mcp = GetOperation(NameRules.McpUserName, "SetDisplayName");
        var person = GetOperation("mom", "SetDisplayName");

        // Assert
        Assert.False(anonymous.IsMethodEnabled());
        Assert.False(mcp.IsMethodEnabled());
        Assert.Equal("Built-in users have no display name.", GetDisabledReason(anonymous));
        Assert.Equal("Built-in users have no display name.", GetDisabledReason(mcp));
        Assert.True(person.IsMethodEnabled());
        Assert.Null(GetDisabledReason(person));
        Assert.True(person.TryGetMethodMetadata()!.RequiresAdministration);
    }

    [Fact]
    public async Task WhenSetDisplayNameRuns_ThenTheAdministrationSetsItForTheUser()
    {
        // Arrange
        var administration = new RecordingUserAdministration();

        // Act
        await _authorization.Users["dad"].SetDisplayNameAsync(administration, "Dad", CancellationToken.None);
        administration.Error = "Refused.";
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _authorization.Users["dad"].SetDisplayNameAsync(administration, null, CancellationToken.None));

        // Assert
        Assert.Equal(["dad Dad", "dad "], administration.Calls);
        Assert.Equal("Refused.", refusal.Message);
    }

    private RegisteredSubjectProperty GetOperation(string userName, string operationName) =>
        _authorization.Users[userName].TryGetRegisteredSubject()!.TryGetProperty(operationName)!;

    private static string? GetDisabledReason(RegisteredSubjectProperty operation) =>
        operation.TryGetAttribute(KnownAttributes.DisabledReason)?.GetValue() as string;

    private sealed class RecordingUserAdministration : IUserAdministration
    {
        public string? Error { get; set; }

        public List<string> Calls { get; } = [];

        public Task<UserChangeResult> SetDisplayNameAsync(string userName, string? displayName, CancellationToken cancellationToken)
        {
            Calls.Add($"{userName} {displayName}");
            return Task.FromResult(new UserChangeResult(Error is null, Error));
        }

        public Task<UserChangeResult> AddUserAsync(string userName, string? email, IReadOnlyCollection<string> roles, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<UserChangeResult> RemoveAsync(string userName, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<UserChangeResult> ResetPasswordAsync(string userName, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<UserChangeResult> SetLockedAsync(string userName, bool isLocked, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<UserChangeResult> SetRolesAsync(string userName, IReadOnlyCollection<string> roles, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<UserChangeResult> CreateMcpKeyAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<UserChangeResult> RevokeMcpKeyAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
