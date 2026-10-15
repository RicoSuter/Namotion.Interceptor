using System.Collections.Immutable;
using HomeBlaze.Services.Diagnostics;
using Xunit;

namespace HomeBlaze.Services.Tests.Diagnostics;

public class InstallationRepositoryTests
{
    private static InventoryAssembly CreateAssembly(string name, bool hasMismatch = false) =>
        new(name, name, "Default", "1.0.0", null, hasMismatch);

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
}
