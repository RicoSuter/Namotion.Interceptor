using System.Reactive.Concurrency;
using HomeBlaze.Authorization.Tests.Subjects;
using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Authorization;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;
using Xunit;

namespace HomeBlaze.Authorization.Tests;

public sealed class RoleOperationsTests : IDisposable
{
    private const string DetachedMessage = "The role is not part of an Authorization subject. It may have been deleted, or its file reloaded.";
    private const string DetachedAuthorizationMessage = "The Authorization subject is not part of the graph. It may have been deleted.";

    private readonly IInterceptorSubjectContext _context = TestContext.Create();
    private readonly ServiceProvider _serializerServices = new ServiceCollection().BuildServiceProvider();
    private readonly RootManager _rootManager;
    private readonly AuthorizationService _service;
    private readonly RecordingConfigurationWriter _writer;
    private readonly Authorization _authorization;

    public RoleOperationsTests()
    {
        var typeProvider = new TypeProvider();
        _rootManager = new RootManager(
            new SubjectTypeRegistry(typeProvider),
            new ConfigurableSubjectSerializer(typeProvider, _serializerServices),
            _context,
            new SubjectPathResolver(() => null));
        _service = new AuthorizationService(_context, new DefaultPathProvider('/'));
        _authorization = new Authorization
        {
            Roles = new Dictionary<string, Role>(StringComparer.OrdinalIgnoreCase)
            {
                ["Kitchen"] = new Role(),
                ["Garden"] = new Role()
            }
        };
        _writer = new RecordingConfigurationWriter(_context)
        {
            Children = new Dictionary<string, IInterceptorSubject>
            {
                ["Authorization"] = _authorization,
                ["Device"] = new TagTestSubject()
            }
        };
    }

    public void Dispose()
    {
        _service.Dispose();
        _rootManager.Dispose();
        _serializerServices.Dispose();
    }

    [Fact]
    public async Task WhenARoleIsAdded_ThenANewDictionaryHoldsTheExistingRolesAndANewEmptyOneAndTheConfigurationIsWrittenOnce()
    {
        // Arrange
        var previousRoles = _authorization.Roles;
        var kitchen = _authorization.Roles["Kitchen"];

        // Act
        await _authorization.AddRoleAsync(" Pantry ", _service, _rootManager);

        // Assert
        Assert.NotSame(previousRoles, _authorization.Roles);
        Assert.Equal(["Kitchen", "Garden", "Pantry"], _authorization.Roles.Keys);
        Assert.Same(kitchen, _authorization.Roles["kitchen"]);
        Assert.Empty(_authorization.Roles["Pantry"].Allow);
        var write = Assert.Single(_writer.Writes);
        Assert.Same(_authorization, write.Subject);
        Assert.Equal(["Kitchen", "Garden", "Pantry"], write.RoleNames);
    }

    [Theory]
    [InlineData("garden", "A role with this name exists already.")]
    [InlineData("reader", "The name 'reader' is reserved for a built-in role.")]
    [InlineData("SYSTEM", "The name 'SYSTEM' is reserved for the system rules.")]
    [InlineData("  ", "Enter a name.")]
    [InlineData("Kitchen/Lights", "The name 'Kitchen/Lights' cannot contain '/', '[' or ']', because it is part of the role's path.")]
    [InlineData("Lights[0]", "The name 'Lights[0]' cannot contain '/', '[' or ']', because it is part of the role's path.")]
    public async Task WhenTheNameIsInvalid_ThenAddingThrowsItsErrorAndChangesAndWritesNothing(string name, string error)
    {
        // Arrange
        var previousRoles = _authorization.Roles;

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _authorization.AddRoleAsync(name, _service, _rootManager));
        Assert.Equal(error, exception.Message);
        Assert.Same(previousRoles, _authorization.Roles);
        Assert.Empty(_writer.Writes);
    }

    [Fact]
    public async Task WhenTheWriterWritesNothing_ThenAddingThrowsAndTheRoleStaysAdded()
    {
        // Arrange
        _writer.IsWriting = false;

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _authorization.AddRoleAsync("Pantry", _service, _rootManager));
        Assert.Equal("The role 'Pantry' was added, but the configuration was not saved and will be lost on restart.", exception.Message);
        Assert.True(_authorization.Roles.ContainsKey("Pantry"));
    }

    [Fact]
    public async Task WhenTheWriteFails_ThenAddingThrowsWithItsErrorAndTheRoleStaysAdded()
    {
        // Arrange
        var writeException = new IOException("The disk is full.");
        _writer.WriteException = writeException;

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _authorization.AddRoleAsync("Pantry", _service, _rootManager));
        Assert.Equal(
            "The role 'Pantry' was added, but the configuration could not be saved (The disk is full.) and will be lost on restart.",
            exception.Message);
        Assert.Same(writeException, exception.InnerException);
        Assert.True(_authorization.Roles.ContainsKey("Pantry"));
    }

    [Fact]
    public async Task WhenTheAuthorizationIsNotInTheGraph_ThenAddingThrowsAndChangesAndWritesNothing()
    {
        // Arrange
        var previousRoles = _authorization.Roles;
        _writer.Children = new Dictionary<string, IInterceptorSubject> { ["Device"] = _writer.Children["Device"] };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _authorization.AddRoleAsync("Pantry", _service, _rootManager));
        Assert.Equal(DetachedAuthorizationMessage, exception.Message);
        Assert.Same(previousRoles, _authorization.Roles);
        Assert.Empty(_writer.Writes);
    }

    [Fact]
    public async Task WhenARoleIsAdded_ThenTheRolesAreAssignedUnderTheEditLock()
    {
        // Arrange
        var isLockHeldPerAssignment = new List<bool>();
        using var subscription = RecordLockPerRolesAssignment(isLockHeldPerAssignment);

        // Act
        await _authorization.AddRoleAsync("Pantry", _service, _rootManager);

        // Assert
        Assert.Equal([true], isLockHeldPerAssignment);
    }

    [Fact]
    public async Task WhenTwoRolesHaveTheSameNameIgnoringCase_ThenAddingThrowsAndChangesNothing()
    {
        // Arrange
        _authorization.Roles = new Dictionary<string, Role>(StringComparer.Ordinal) { ["Kitchen"] = new Role(), ["KITCHEN"] = new Role() };
        var previousRoles = _authorization.Roles;

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _authorization.AddRoleAsync("Pantry", _service, _rootManager));
        Assert.Equal("Several roles are named 'KITCHEN', ignoring case. Rename or delete one of them first.", exception.Message);
        Assert.Same(previousRoles, _authorization.Roles);
        Assert.Empty(_writer.Writes);
    }

    [Fact]
    public async Task WhenOneOfTwoRolesWithTheSameNameIgnoringCaseIsDeleted_ThenTheOtherStays()
    {
        // Arrange
        var upper = new Role();
        _authorization.Roles = new Dictionary<string, Role>(StringComparer.Ordinal) { ["Kitchen"] = new Role(), ["KITCHEN"] = upper };
        var lower = _authorization.Roles["Kitchen"];

        // Act
        await lower.DeleteAsync(_service, _rootManager);

        // Assert
        Assert.Equal(["KITCHEN"], _authorization.Roles.Keys);
        Assert.Same(upper, _authorization.Roles["kitchen"]);
    }

    [Fact]
    public async Task WhenARoleIsRenamed_ThenTheSameRoleWithItsRulesIsHeldUnderTheNewNameAndTheConfigurationIsWrittenOnce()
    {
        // Arrange
        var kitchen = _authorization.Roles["Kitchen"];
        var rule = new PermissionRule { Paths = ["/Device/**"], Actions = [PermissionAction.Read] };
        kitchen.Allow = [rule];

        // Act
        await kitchen.RenameAsync("Pantry", _service, _rootManager);

        // Assert
        Assert.Equal(["Garden", "Pantry"], _authorization.Roles.Keys);
        Assert.Same(kitchen, _authorization.Roles["Pantry"]);
        Assert.Equal([rule], kitchen.Allow);
        Assert.Equal(["Pantry"], kitchen.TryGetRegisteredSubject()!.Parents.Select(parent => parent.Index));
        Assert.Equal(["Garden", "Pantry"], Assert.Single(_writer.Writes).RoleNames);
    }

    [Fact]
    public async Task WhenARoleIsRenamedOnlyInCase_ThenItKeepsItsInstanceAndItsAgentsKeepTheirPolicy()
    {
        // Arrange
        var kitchen = _authorization.Roles["Kitchen"];
        kitchen.Allow = [new PermissionRule { Members = [nameof(TagTestSubject.Plain)], Actions = [PermissionAction.Read] }];
        var plain = PlainMember();
        Assert.True(_service.GetPolicy(["Kitchen"]).Policy.CanRead(plain));

        // Act
        await kitchen.RenameAsync("KITCHEN", _service, _rootManager);

        // Assert
        Assert.Equal(["Garden", "KITCHEN"], _authorization.Roles.Keys);
        Assert.Same(kitchen, _authorization.Roles["KITCHEN"]);
        Assert.Equal(["KITCHEN"], kitchen.TryGetRegisteredSubject()!.Parents.Select(parent => parent.Index));
        var result = _service.GetPolicy(["Kitchen"]);
        Assert.Null(result.Error);
        Assert.True(result.Policy.CanRead(plain));
    }

    [Theory]
    [InlineData("garden", "A role with this name exists already.")]
    [InlineData("reader", "The name 'reader' is reserved for a built-in role.")]
    [InlineData("  ", "Enter a name.")]
    [InlineData("Kitchen/Lights", "The name 'Kitchen/Lights' cannot contain '/', '[' or ']', because it is part of the role's path.")]
    public async Task WhenTheNewNameIsInvalid_ThenRenamingThrowsItsErrorAndChangesAndWritesNothing(string name, string error)
    {
        // Arrange
        var previousRoles = _authorization.Roles;

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _authorization.Roles["Kitchen"].RenameAsync(name, _service, _rootManager));
        Assert.Equal(error, exception.Message);
        Assert.Same(previousRoles, _authorization.Roles);
        Assert.Empty(_writer.Writes);
    }

    [Fact]
    public async Task WhenTheNameIsUnchanged_ThenRenamingAssignsAndWritesNothing()
    {
        // Arrange
        var previousRoles = _authorization.Roles;

        // Act
        await _authorization.Roles["Kitchen"].RenameAsync(" Kitchen ", _service, _rootManager);

        // Assert
        Assert.Same(previousRoles, _authorization.Roles);
        Assert.Empty(_writer.Writes);
    }

    [Fact]
    public async Task WhenTheNameIsUnchangedAndAnotherRoleHasItInAnotherCase_ThenRenamingAssignsAndWritesNothing()
    {
        // Arrange
        _authorization.Roles = new Dictionary<string, Role>(StringComparer.Ordinal) { ["Kitchen"] = new Role(), ["KITCHEN"] = new Role() };
        var previousRoles = _authorization.Roles;

        // Act
        await _authorization.Roles["Kitchen"].RenameAsync("Kitchen", _service, _rootManager);

        // Assert
        Assert.Same(previousRoles, _authorization.Roles);
        Assert.Empty(_writer.Writes);
    }

    [Fact]
    public async Task WhenTheWriterWritesNothing_ThenRenamingThrowsAndTheRoleStaysRenamed()
    {
        // Arrange
        var kitchen = _authorization.Roles["Kitchen"];
        _writer.IsWriting = false;

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => kitchen.RenameAsync("Pantry", _service, _rootManager));
        Assert.Equal("The role was renamed to 'Pantry', but the configuration was not saved and will be lost on restart.", exception.Message);
        Assert.Same(kitchen, _authorization.Roles["Pantry"]);
    }

    [Fact]
    public async Task WhenTheWriteFails_ThenRenamingThrowsWithItsErrorAndTheRoleStaysRenamed()
    {
        // Arrange
        var kitchen = _authorization.Roles["Kitchen"];
        var writeException = new IOException("The disk is full.");
        _writer.WriteException = writeException;

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => kitchen.RenameAsync("Pantry", _service, _rootManager));
        Assert.Equal(
            "The role was renamed to 'Pantry', but the configuration could not be saved (The disk is full.) and will be lost on restart.",
            exception.Message);
        Assert.Same(writeException, exception.InnerException);
        Assert.Same(kitchen, _authorization.Roles["Pantry"]);
    }

    [Fact]
    public async Task WhenARoleIsRenamed_ThenBothAssignmentsOfTheRolesAreMadeUnderTheEditLock()
    {
        // Arrange
        var isLockHeldPerAssignment = new List<bool>();
        using var subscription = RecordLockPerRolesAssignment(isLockHeldPerAssignment);

        // Act
        await _authorization.Roles["Kitchen"].RenameAsync("Pantry", _service, _rootManager);

        // Assert
        Assert.Equal([true, true], isLockHeldPerAssignment);
    }

    [Fact]
    public async Task WhenTheRoleWasDeleted_ThenRenamingAndDeletingItThrowAndChangeNothing()
    {
        // Arrange
        var kitchen = _authorization.Roles["Kitchen"];
        _authorization.Roles = new Dictionary<string, Role>(StringComparer.OrdinalIgnoreCase) { ["Garden"] = _authorization.Roles["Garden"] };

        // Act & Assert
        var renameException = await Assert.ThrowsAsync<InvalidOperationException>(() => kitchen.RenameAsync("Pantry", _service, _rootManager));
        var deleteException = await Assert.ThrowsAsync<InvalidOperationException>(() => kitchen.DeleteAsync(_service, _rootManager));
        Assert.Equal(DetachedMessage, renameException.Message);
        Assert.Equal(DetachedMessage, deleteException.Message);
        Assert.Equal(["Garden"], _authorization.Roles.Keys);
        Assert.Empty(_writer.Writes);
    }

    [Fact]
    public async Task WhenTheRoleIsHeldByNoAuthorization_ThenRenamingThrows()
    {
        // Arrange
        var role = new Role(_context);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => role.RenameAsync("Pantry", _service, _rootManager));
        Assert.Equal(DetachedMessage, exception.Message);
    }

    [Fact]
    public async Task WhenARoleIsDeleted_ThenItIsLeftOutOfANewDictionaryAndTheConfigurationIsWrittenOnce()
    {
        // Arrange
        var previousRoles = _authorization.Roles;

        // Act
        await _authorization.Roles["Garden"].DeleteAsync(_service, _rootManager);

        // Assert
        Assert.NotSame(previousRoles, _authorization.Roles);
        Assert.Equal(["Kitchen"], _authorization.Roles.Keys);
        Assert.Equal(["Kitchen"], Assert.Single(_writer.Writes).RoleNames);
    }

    [Fact]
    public async Task WhenTheWriterWritesNothing_ThenDeletingThrowsAndTheRoleStaysDeleted()
    {
        // Arrange
        _writer.IsWriting = false;

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _authorization.Roles["Garden"].DeleteAsync(_service, _rootManager));
        Assert.Equal("The role 'Garden' was deleted, but the configuration was not saved and will be lost on restart.", exception.Message);
        Assert.Equal(["Kitchen"], _authorization.Roles.Keys);
    }

    [Fact]
    public async Task WhenTheWriteFails_ThenDeletingThrowsWithItsErrorAndTheRoleStaysDeleted()
    {
        // Arrange
        var writeException = new IOException("The disk is full.");
        _writer.WriteException = writeException;

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _authorization.Roles["Garden"].DeleteAsync(_service, _rootManager));
        Assert.Equal(
            "The role 'Garden' was deleted, but the configuration could not be saved (The disk is full.) and will be lost on restart.",
            exception.Message);
        Assert.Same(writeException, exception.InnerException);
        Assert.Equal(["Kitchen"], _authorization.Roles.Keys);
    }

    [Fact]
    public async Task WhenARoleThatAgentsNameIsDeleted_ThenTheirPolicyReportsItAsUnknown()
    {
        // Act
        await _authorization.Roles["Garden"].DeleteAsync(_service, _rootManager);

        // Assert
        var result = _service.GetPolicy(["Garden"]);
        Assert.Same(AuthorizationPolicy.DenyAll, result.Policy);
        Assert.Contains("'Garden'", result.Error);
    }

    [Fact]
    public void WhenTheOperationsAreRegistered_ThenTheyAreNamedWithoutAsyncAndRenameAndDeleteRequireConfirmation()
    {
        // Arrange
        var authorization = _authorization.TryGetRegisteredSubject()!;
        var kitchen = _authorization.Roles["Kitchen"].TryGetRegisteredSubject()!;

        // Act
        var addRole = authorization.TryGetProperty("AddRole")?.TryGetMethodMetadata();
        var rename = kitchen.TryGetProperty("Rename")?.TryGetMethodMetadata();
        var delete = kitchen.TryGetProperty("Delete")?.TryGetMethodMetadata();

        // Assert
        Assert.False(addRole!.RequiresConfirmation);
        Assert.True(rename!.RequiresConfirmation);
        Assert.True(delete!.RequiresConfirmation);
        Assert.Equal("Add Role", addRole.Title);
        Assert.Equal("Add", addRole.Icon);
        Assert.Equal("Rename", rename.Title);
        Assert.Equal("DriveFileRenameOutline", rename.Icon);
        Assert.Equal("Delete", delete.Title);
        Assert.Equal("Delete", delete.Icon);
    }

    private RegisteredSubjectProperty PlainMember() =>
        _writer.Children["Device"].TryGetRegisteredSubject()!.TryGetProperty(nameof(TagTestSubject.Plain))!;

    // Records, per assignment of Authorization.Roles, whether the edit lock is held by the assigning thread.
    private IDisposable RecordLockPerRolesAssignment(List<bool> isLockHeldPerAssignment) =>
        _context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(change =>
            {
                if (change.Property.Name == nameof(Authorization.Roles))
                {
                    isLockHeldPerAssignment.Add(AuthorizationEditLock.Instance.IsHeldByCurrentThread);
                }
            });
}
