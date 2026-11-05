using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Services;
using Namotion.Interceptor;
using Namotion.Interceptor.Authorization;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Xunit;

namespace HomeBlaze.Authorization.Tests;

public sealed class RoleOperationsTests
{
    private const string DetachedMessage = "The role is not part of an Authorization subject. It may have been deleted.";
    private const string DetachedAuthorizationMessage = "The Authorization subject is not part of the graph. It may have been deleted.";

    private readonly Authorization _authorization;
    private readonly RecordingRoleStore _roleStore;

    public RoleOperationsTests()
    {
        _authorization = new Authorization();
        _ = new TestRoot(TestContext.Create())
        {
            Children = new Dictionary<string, IInterceptorSubject> { ["Authorization"] = _authorization }
        };
        _authorization.ReplaceRoles([new AuthorizationRole { Name = "kitchen" }, new AuthorizationRole { Name = "garden" }]);
        _roleStore = new RecordingRoleStore(_authorization);
    }

    [Fact]
    public async Task WhenARoleIsAdded_ThenTheStoreSavesItTrimmedWithoutRulesAndTheRoleIsShown()
    {
        // Act
        await _authorization.AddRoleAsync(" pantry ", _roleStore, CancellationToken.None);

        // Assert
        var (originalName, role, stamp) = Assert.Single(_roleStore.Saves);
        Assert.Null(originalName);
        Assert.Null(stamp);
        Assert.Equal("pantry", role.Name);
        Assert.Empty(role.Includes);
        Assert.Empty(role.Allow);
        Assert.Empty(role.Deny);
        Assert.True(_authorization.Roles.ContainsKey("pantry"));
    }

    [Fact]
    public async Task WhenTheNameIsBlank_ThenAddingThrowsWithoutAskingTheStore()
    {
        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => _authorization.AddRoleAsync("  ", _roleStore, CancellationToken.None));
        Assert.Equal("Enter a name.", exception.Message);
        Assert.Empty(_roleStore.Saves);
    }

    [Fact]
    public async Task WhenTheStoreRefusesTheRole_ThenAddingThrowsItsErrorAndTheRolesStayAsTheyWere()
    {
        // Arrange
        _roleStore.Refusal = "A role named 'garden' already exists.";
        var roles = _authorization.Roles;

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => _authorization.AddRoleAsync("garden", _roleStore, CancellationToken.None));
        Assert.Equal("A role named 'garden' already exists.", exception.Message);
        Assert.Same(roles, _authorization.Roles);
    }

    [Fact]
    public async Task WhenTheAuthorizationIsNotInTheGraph_ThenAddingThrowsWithoutAskingTheStore()
    {
        // Arrange
        var detached = new Authorization();

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => detached.AddRoleAsync("pantry", _roleStore, CancellationToken.None));
        Assert.Equal(DetachedAuthorizationMessage, exception.Message);
        Assert.Empty(_roleStore.Saves);
    }

    [Fact]
    public async Task WhenARoleIsRenamed_ThenTheStoreRenamesItByItsHeldNameToTheTrimmedName()
    {
        // Arrange
        var kitchen = _authorization.Roles["kitchen"];

        // Act
        await kitchen.RenameAsync(" pantry ", _roleStore, CancellationToken.None);

        // Assert
        Assert.Equal([("kitchen", "pantry")], _roleStore.Renames);
        Assert.True(_authorization.Roles.ContainsKey("pantry"));
        Assert.False(_authorization.Roles.ContainsKey("kitchen"));
    }

    [Fact]
    public async Task WhenTheNameIsUnchanged_ThenRenamingAsksNothingOfTheStore()
    {
        // Arrange
        var kitchen = _authorization.Roles["kitchen"];

        // Act
        await kitchen.RenameAsync("kitchen", _roleStore, CancellationToken.None);

        // Assert
        Assert.Empty(_roleStore.Renames);
    }

    [Fact]
    public async Task WhenTheStoreRefusesTheRename_ThenRenamingThrowsItsError()
    {
        // Arrange
        _roleStore.Refusal = "The name 'reader' is reserved for a built-in role.";

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _authorization.Roles["kitchen"].RenameAsync("reader", _roleStore, CancellationToken.None));
        Assert.Equal("The name 'reader' is reserved for a built-in role.", exception.Message);
    }

    [Fact]
    public async Task WhenTheRenameIsCommittedWithAWarning_ThenRenamingThrowsTheWarning()
    {
        // Arrange
        _roleStore.Warning = "The role was renamed to 'pantry' and the principals use the new name, but it could not be saved everywhere.";

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _authorization.Roles["kitchen"].RenameAsync("pantry", _roleStore, CancellationToken.None));
        Assert.Equal(_roleStore.Warning, exception.Message);
        Assert.True(_authorization.Roles.ContainsKey("pantry"));
    }

    [Fact]
    public async Task WhenTheRoleWasReplacedSinceItWasShown_ThenRenamingAndDeletingItThrowWithoutAskingTheStore()
    {
        // Arrange: the store replaces every role on each change.
        var kitchen = _authorization.Roles["kitchen"];
        await _authorization.AddRoleAsync("pantry", _roleStore, CancellationToken.None);

        // Act & Assert
        var rename = await Assert.ThrowsAsync<InvalidOperationException>(() => kitchen.RenameAsync("cellar", _roleStore, CancellationToken.None));
        var delete = await Assert.ThrowsAsync<InvalidOperationException>(() => kitchen.DeleteAsync(_roleStore, CancellationToken.None));
        Assert.Equal(DetachedMessage, rename.Message);
        Assert.Equal(DetachedMessage, delete.Message);
        Assert.Empty(_roleStore.Renames);
        Assert.Empty(_roleStore.Deletes);
    }

    [Fact]
    public async Task WhenTheRoleIsReplacedWhileItsStampIsRead_ThenDeletingItThrowsWithoutAskingTheStore()
    {
        // Arrange: the stamp then belongs to rules this subject does not have.
        var kitchen = _authorization.Roles["kitchen"];
        _roleStore.StampReading = () => _authorization.ReplaceRoles(
            [.. _authorization.Roles.Keys.Select(name => new AuthorizationRole { Name = name })]);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => kitchen.DeleteAsync(_roleStore, CancellationToken.None));
        Assert.Equal(DetachedMessage, exception.Message);
        Assert.Empty(_roleStore.Deletes);
    }

    [Fact]
    public async Task WhenTheRoleIsHeldByNoAuthorization_ThenRenamingThrows()
    {
        // Arrange
        var role = new Role(TestContext.Create());

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => role.RenameAsync("pantry", _roleStore, CancellationToken.None));
        Assert.Equal(DetachedMessage, exception.Message);
    }

    [Fact]
    public async Task WhenARoleIsDeleted_ThenTheStoreDeletesItWithItsStamp()
    {
        // Arrange
        var kitchen = _authorization.Roles["kitchen"];

        // Act
        await kitchen.DeleteAsync(_roleStore, CancellationToken.None);

        // Assert
        Assert.Equal([("kitchen", RecordingRoleStore.GetStamp("kitchen"))], _roleStore.Deletes);
        Assert.False(_authorization.Roles.ContainsKey("kitchen"));
    }

    [Fact]
    public async Task WhenTheStoreRefusesTheDelete_ThenDeletingThrowsItsErrorAndTheRoleStays()
    {
        // Arrange
        _roleStore.Refusal = "The role 'kitchen' is used by users or agents; remove it from them first.";

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _authorization.Roles["kitchen"].DeleteAsync(_roleStore, CancellationToken.None));
        Assert.Equal(_roleStore.Refusal, exception.Message);
        Assert.True(_authorization.Roles.ContainsKey("kitchen"));
    }

    [Fact]
    public void WhenTheOperationsAreRegistered_ThenTheyRequireAdministrationAndRenameAndDeleteRequireConfirmation()
    {
        // Arrange
        var authorization = _authorization.TryGetRegisteredSubject()!;
        var kitchen = _authorization.Roles["kitchen"].TryGetRegisteredSubject()!;

        // Act
        var addRole = authorization.TryGetProperty("AddRole")?.TryGetMethodMetadata();
        var rename = kitchen.TryGetProperty("Rename")?.TryGetMethodMetadata();
        var delete = kitchen.TryGetProperty("Delete")?.TryGetMethodMetadata();

        // Assert
        Assert.True(addRole!.RequiresAdministration);
        Assert.True(rename!.RequiresAdministration);
        Assert.True(delete!.RequiresAdministration);
        Assert.False(addRole.RequiresConfirmation);
        Assert.True(rename.RequiresConfirmation);
        Assert.True(delete.RequiresConfirmation);
        Assert.Equal("Add Role", addRole.Title);
        Assert.Equal("Add", addRole.Icon);
        Assert.Equal("Rename", rename.Title);
        Assert.Equal("DriveFileRenameOutline", rename.Icon);
        Assert.Equal("Delete", delete.Title);
        Assert.Equal("Delete", delete.Icon);
    }

    [Fact]
    public void WhenTheDeleteOperationOfARoleIsLookedUp_ThenItIsTheRolesDeleteOperation()
    {
        // Arrange
        var kitchen = _authorization.Roles["kitchen"].TryGetRegisteredSubject()!;

        // Act
        var deleteOperation = kitchen.GetDeleteOperation(ExclusionTarget.UserInterface);

        // Assert
        Assert.NotNull(deleteOperation);
        Assert.Equal("Delete", deleteOperation.PropertyName);
        Assert.Equal(OperationPurpose.Delete, deleteOperation.Purpose);
        Assert.True(deleteOperation.RequiresConfirmation);
        Assert.DoesNotContain(kitchen.GetAllMethods(ExclusionTarget.UserInterface, excludeMethodsWithPurpose: true), method => method.PropertyName == "Delete");
    }

    // Records the changes and, unless told to refuse, applies them to the Authorization as the role store does: every
    // role gets a new subject.
    private sealed class RecordingRoleStore(Authorization authorization) : IRoleStore
    {
        public List<(string? OriginalName, AuthorizationRole Role, string? Stamp)> Saves { get; } = [];

        public List<(string OldName, string NewName)> Renames { get; } = [];

        public List<(string Name, string? Stamp)> Deletes { get; } = [];

        public string? Refusal { get; set; }

        public string? Warning { get; set; }

        public static string GetStamp(string name) => $"stamp-{name.ToUpperInvariant()}";

        public Task<RoleChangeResult> SaveRoleAsync(string? originalName, AuthorizationRole role, string? concurrencyStamp, CancellationToken cancellationToken)
        {
            Saves.Add((originalName, role, concurrencyStamp));
            return Task.FromResult(Apply(roles => roles.Add(role)));
        }

        public Task<RoleChangeResult> RenameRoleAsync(string oldName, string newName, CancellationToken cancellationToken)
        {
            Renames.Add((oldName, newName));
            return Task.FromResult(Apply(roles =>
            {
                var index = roles.FindIndex(role => string.Equals(role.Name, oldName, StringComparison.OrdinalIgnoreCase));
                roles[index] = roles[index] with { Name = newName };
            }));
        }

        public Task<RoleChangeResult> DeleteRoleAsync(string name, string? concurrencyStamp, CancellationToken cancellationToken)
        {
            Deletes.Add((name, concurrencyStamp));
            return Task.FromResult(Apply(roles => roles.RemoveAll(role => string.Equals(role.Name, name, StringComparison.OrdinalIgnoreCase))));
        }

        /// <summary>Runs right before <see cref="GetConcurrencyStamp"/> answers.</summary>
        public Action? StampReading { get; set; }

        public string? GetConcurrencyStamp(string name)
        {
            StampReading?.Invoke();
            return authorization.Roles.ContainsKey(name) ? GetStamp(name) : null;
        }

        private RoleChangeResult Apply(Action<List<AuthorizationRole>> change)
        {
            if (Refusal is { } refusal)
            {
                return new RoleChangeResult(false, refusal);
            }

            var roles = authorization.Roles
                .Select(entry => new AuthorizationRole
                {
                    Name = entry.Key,
                    Includes = [.. entry.Value.Includes],
                    Allow = [.. entry.Value.Allow],
                    Deny = [.. entry.Value.Deny]
                })
                .ToList();
            change(roles);
            authorization.ReplaceRoles(roles);
            return new RoleChangeResult(true, null, Warning);
        }
    }
}
