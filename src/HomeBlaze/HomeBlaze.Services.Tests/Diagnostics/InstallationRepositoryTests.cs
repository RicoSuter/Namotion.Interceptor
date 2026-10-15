using HomeBlaze.Services.Diagnostics;
using Xunit;

namespace HomeBlaze.Services.Tests.Diagnostics;

public class InstallationRepositoryTests
{
    private static InventoryAssembly CreateAssembly(string name, string version = "1.0.0", string? commit = null) =>
        new(name, name, "Default", version, commit);

    private static InventoryRepository CreateEntry(params InventoryAssembly[] assemblies) =>
        new("repo", "https://github.com/owner/repo", [.. assemblies]);

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
        repository.Apply(CreateEntry(CreateAssembly("A"), CreateAssembly("B")));
        var assemblies = repository.Assemblies;
        var first = assemblies["A"];

        // Act
        repository.Apply(CreateEntry(CreateAssembly("A"), CreateAssembly("B")));

        // Assert
        Assert.Same(assemblies, repository.Assemblies);
        Assert.Same(first, repository.Assemblies["A"]);
    }

    [Fact]
    public void WhenAnAssemblyIsInAPluginContext_ThenTitleNamesTheContext()
    {
        // Act
        var assembly = new InstallationAssembly(new InventoryAssembly("Lib (Plugin)", "Lib", "Plugin", "1.0.0", "abcdef0"));

        // Assert
        Assert.Equal("Lib (Plugin)", assembly.Title);
        Assert.Equal("Plugin", assembly.LoadContext);
    }

    [Fact]
    public void WhenAnAssemblyVersionChanges_ThenTheReusedSubjectShowsTheNewVersion()
    {
        // Arrange
        var repository = new InstallationRepository("repo", null);
        repository.Apply(CreateEntry(CreateAssembly("A")));
        var first = repository.Assemblies["A"];

        // Act
        repository.Apply(CreateEntry(CreateAssembly("A", version: "2.0.0", commit: "abcdef0")));

        // Assert
        Assert.Same(first, repository.Assemblies["A"]);
        Assert.Equal("2.0.0", first.Version);
        Assert.Equal("abcdef0", first.Commit);
    }

    [Theory]
    [InlineData("A")]
    [InlineData("A", "C")]
    [InlineData("A", "B", "C")]
    public void WhenTheAssembliesChange_ThenANewDictionaryKeepsTheSubjectsThatStay(params string[] names)
    {
        // Arrange
        var repository = new InstallationRepository("repo", null);
        repository.Apply(CreateEntry(CreateAssembly("A"), CreateAssembly("B")));
        var assemblies = repository.Assemblies;
        var first = assemblies["A"];

        // Act
        repository.Apply(CreateEntry(names.Select(name => CreateAssembly(name)).ToArray()));

        // Assert
        Assert.NotSame(assemblies, repository.Assemblies);
        Assert.Equal(names, repository.Assemblies.Keys);
        Assert.Same(first, repository.Assemblies["A"]);
    }
}
