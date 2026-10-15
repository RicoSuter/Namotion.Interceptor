using System.Reactive.Concurrency;
using System.Reactive.Linq;
using HomeBlaze.Services.Diagnostics;
using Namotion.Interceptor;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Xunit;

namespace HomeBlaze.Services.Tests.Diagnostics;

public class InstallationRepositoryTests
{
    private static InventoryAssembly CreateAssembly(string name, bool hasMismatch = false, string version = "1.0.0", string? commit = null) =>
        new(name, name, "Default", version, commit, hasMismatch);

    private static InventoryRepository CreateEntry(bool hasMismatch, params InventoryAssembly[] assemblies) =>
        new("repo", "https://github.com/owner/repo", hasMismatch, [.. assemblies]);

    [Fact]
    public void WhenCreated_ThenHasNameAndUrl()
    {
        // Act
        var repository = new InstallationRepository("repo", "https://github.com/owner/repo");

        // Assert
        Assert.Equal("repo", repository.Title);
        Assert.Equal(new Uri("https://github.com/owner/repo"), repository.RepositoryUrl);
        Assert.Empty(repository.Assemblies);
    }

    [Fact]
    public void WhenAppliedTwiceWithTheSameAssemblies_ThenKeepsTheSubjectsAndTheDictionary()
    {
        // Arrange
        var repository = new InstallationRepository("repo", null);
        repository.Apply(CreateEntry(false, CreateAssembly("A"), CreateAssembly("B")));
        var assemblies = repository.Assemblies;
        var first = assemblies["A"];

        // Act
        repository.Apply(CreateEntry(false, CreateAssembly("A", hasMismatch: true), CreateAssembly("B")));

        // Assert
        Assert.Same(assemblies, repository.Assemblies);
        Assert.Same(first, repository.Assemblies["A"]);
        Assert.True(first.HasMismatch);
    }

    [Fact]
    public void WhenAnAssemblyIsAdded_ThenKeepsTheExistingSubjects()
    {
        // Arrange
        var repository = new InstallationRepository("repo", null);
        repository.Apply(CreateEntry(false, CreateAssembly("A")));
        var first = repository.Assemblies["A"];

        // Act
        repository.Apply(CreateEntry(true, CreateAssembly("A"), CreateAssembly("B")));

        // Assert
        Assert.Equal(new[] { "A", "B" }, repository.Assemblies.Keys);
        Assert.Same(first, repository.Assemblies["A"]);
        Assert.True(repository.HasMismatch);
        Assert.Equal("Warning", repository.IconColor);
    }

    [Fact]
    public void WhenAnAssemblyIsInAPluginContext_ThenTitleNamesTheContext()
    {
        // Act
        var assembly = new InstallationAssembly(new InventoryAssembly("Lib (Plugin)", "Lib", "Plugin", "1.0.0", "abcdef0", false));

        // Assert
        Assert.Equal("Lib (Plugin)", assembly.Title);
        Assert.Equal("Plugin", assembly.LoadContext);
    }

    [Fact]
    public void WhenAnAssemblyVersionChanges_ThenTheReusedSubjectShowsTheNewVersion()
    {
        // Arrange
        var repository = new InstallationRepository("repo", null);
        repository.Apply(CreateEntry(false, CreateAssembly("A")));
        var first = repository.Assemblies["A"];

        // Act
        repository.Apply(CreateEntry(false, CreateAssembly("A", version: "2.0.0", commit: "abcdef0")));

        // Assert
        Assert.Same(first, repository.Assemblies["A"]);
        Assert.Equal("2.0.0", first.Version);
        Assert.Equal("abcdef0", first.Commit);
    }

    [Fact]
    public void WhenAnAssemblyIsRemoved_ThenReplacesTheDictionary()
    {
        // Arrange
        var repository = new InstallationRepository("repo", null);
        repository.Apply(CreateEntry(false, CreateAssembly("A"), CreateAssembly("B")));
        var assemblies = repository.Assemblies;

        // Act
        repository.Apply(CreateEntry(false, CreateAssembly("A")));

        // Assert
        Assert.NotSame(assemblies, repository.Assemblies);
        Assert.Equal(new[] { "A" }, repository.Assemblies.Keys);
    }

    [Fact]
    public void WhenKeysChangeWithTheSameCount_ThenReplacesTheDictionary()
    {
        // Arrange
        var repository = new InstallationRepository("repo", null);
        repository.Apply(CreateEntry(false, CreateAssembly("A"), CreateAssembly("B")));
        var assemblies = repository.Assemblies;

        // Act
        repository.Apply(CreateEntry(false, CreateAssembly("A"), CreateAssembly("C")));

        // Assert
        Assert.NotSame(assemblies, repository.Assemblies);
        Assert.Equal(new[] { "A", "C" }, repository.Assemblies.Keys);
    }

    [Fact]
    public void WhenTrackedAndAChildAssemblyHasAMismatch_ThenIconColorIsWarning()
    {
        // Arrange: attach to a context that tracks derived property dependencies across subjects,
        // then apply once so the IconColor getter runs while the assembly is attached and records
        // a dependency on its HasMismatch, rather than only on the un-dereferenced dictionary.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var repository = new InstallationRepository("repo", null);
        ((IInterceptorSubject)repository).Context.AddFallbackContext(context);
        repository.Apply(CreateEntry(false, CreateAssembly("A")));

        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Where(change => change.Property.Name == nameof(InstallationRepository.IconColor) &&
                              ReferenceEquals(change.Property.Subject, repository))
            .Subscribe(change => changes.Add(change));

        // Act: the in-place path only writes the child assembly's HasMismatch; the repository's own
        // HasMismatch stays false, so a passing IconColor change can only come from dependency tracking
        // picking up the child write, not from the repository's own property changing.
        repository.Apply(CreateEntry(false, CreateAssembly("A", hasMismatch: true)));

        // Assert
        Assert.False(repository.HasMismatch);
        var change = Assert.Single(changes);
        Assert.Equal("Warning", change.GetNewValue<string?>());
    }
}
