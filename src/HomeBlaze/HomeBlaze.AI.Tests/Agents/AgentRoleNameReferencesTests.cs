using HomeBlaze.AI.Agents;
using HomeBlaze.Authorization;
using HomeBlaze.Services;
using HomeBlaze.Storage.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Attributes;
using Namotion.Interceptor.Tracking;
using Xunit;

namespace HomeBlaze.AI.Tests.Agents;

public sealed class AgentRoleNameReferencesTests : IDisposable
{
    private readonly ServiceProvider _serializerServices = new ServiceCollection().BuildServiceProvider();
    private readonly AgentTestRoot _root;
    private readonly RecordingFolder _folder;
    private readonly RootManager _rootManager;
    private readonly AgentServices _services;

    public AgentRoleNameReferencesTests()
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry().WithParents();
        _root = new AgentTestRoot(context);
        _folder = new RecordingFolder();
        _root.Children = new Dictionary<string, IInterceptorSubject> { ["Agents"] = _folder };

        var typeProvider = new TypeProvider();
        _rootManager = new RootManager(
            new SubjectTypeRegistry(typeProvider),
            new ConfigurableSubjectSerializer(typeProvider, _serializerServices),
            context,
            new SubjectPathResolver(() => _root));
        _services = AgentTestContext.CreateServices(_root);
    }

    public void Dispose()
    {
        _services.AuthorizationService.Dispose();
        _rootManager.Dispose();
        _serializerServices.Dispose();
    }

    [Fact]
    public async Task WhenARoleIsRenamed_ThenTheAgentsNamingItGetTheNewNameAndTheirConfigurationIsWritten()
    {
        // Arrange
        var family = new FakeAgent(_services) { Name = "Family helper", Roles = ["family", BuiltInRoles.ReaderName] };
        var other = new FakeAgent(_services) { Name = "Other helper", Roles = [BuiltInRoles.OperatorName] };
        _folder.Children = new Dictionary<string, IInterceptorSubject> { ["Family"] = family, ["Other"] = other };
        var references = new AgentRoleNameReferences(((IInterceptorSubject)_root).Context, _rootManager);

        // Act
        await references.RenameAsync("family", "household", CancellationToken.None);

        // Assert
        Assert.Equal(["household", BuiltInRoles.ReaderName], family.Roles);
        Assert.Equal([BuiltInRoles.OperatorName], other.Roles);
        Assert.Equal([family], _folder.Written);
        Assert.False(references.IsUsed("family"));
        Assert.True(references.IsUsed("household"));
        Assert.False(references.IsUsed("HOUSEHOLD"));
        var roleSet = Assert.Single(references.GetRoleSets(), roleSet => roleSet.Principal == family.Title);
        Assert.Equal(["household", BuiltInRoles.ReaderName], roleSet.RoleNames);
    }

    [Fact]
    public async Task WhenTheConfigurationOfAnAgentIsNotWritten_ThenTheRenameFailsNamingTheAgent()
    {
        // Arrange
        var family = new FakeAgent(_services) { Name = "Helper", Roles = ["family"] };
        _folder.Children = new Dictionary<string, IInterceptorSubject> { ["Family"] = family };
        _folder.IsWriting = false;
        var references = new AgentRoleNameReferences(((IInterceptorSubject)_root).Context, _rootManager);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => references.RenameAsync("family", "household", CancellationToken.None));
        Assert.Contains("Helper", exception.Message);
    }
}

/// <summary>A folder that records the subjects whose configuration it is asked to write.</summary>
[InterceptorSubject]
public partial class RecordingFolder : IConfigurationWriter
{
    public RecordingFolder()
    {
        Children = new Dictionary<string, IInterceptorSubject>();
    }

    [InlinePaths]
    public partial Dictionary<string, IInterceptorSubject> Children { get; set; }

    /// <summary>Whether a write succeeds.</summary>
    public bool IsWriting { get; set; } = true;

    public List<IInterceptorSubject> Written { get; } = [];

    public Task<bool> WriteConfigurationAsync(IInterceptorSubject subject, CancellationToken cancellationToken)
    {
        Written.Add(subject);
        return Task.FromResult(IsWriting);
    }
}
