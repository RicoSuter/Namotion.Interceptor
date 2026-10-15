using HomeBlaze.Services.Diagnostics;
using Xunit;

namespace HomeBlaze.Services.Tests.Diagnostics;

public class AssemblyInventoryTests
{
    private const string OwnRepositoryUrl = "https://github.com/RicoSuter/Namotion.Interceptor";
    private const string CommitA = "b1b7c2f4568b7a83baf8d041afaab8f2214fb6cd";
    private const string CommitB = "9e41a0c4568b7a83baf8d041afaab8f2214fb6cd";

    [Fact]
    public void WhenAssembliesComeFromRepositories_ThenTheyAreGroupedAndUnknownIsLast()
    {
        // Arrange
        LoadedAssembly[] assemblies =
        [
            new("SomeLibrary", "Default", "1.2.0", null, null),
            new("Namotion.Interceptor", "Default", "0.5.0", CommitA, OwnRepositoryUrl),
            new("HomeBlaze", "Default", "0.5.0", CommitA, OwnRepositoryUrl),
            new("MudBlazor", "Default", "9.2.0", null, "https://github.com/MudBlazor/MudBlazor"),
        ];

        // Act
        var inventory = AssemblyInventory.Create(assemblies, OwnRepositoryUrl);

        // Assert
        Assert.Equal(new[] { "MudBlazor", "Namotion.Interceptor", "Unknown" }, inventory.Select(repository => repository.Name));
        Assert.Equal(new[] { "HomeBlaze", "Namotion.Interceptor" }, inventory[1].Assemblies.Select(assembly => assembly.Name));
        Assert.Null(inventory[2].RepositoryUrl);
        Assert.All(inventory, repository => Assert.False(repository.HasAnyMismatch));
    }

    [Fact]
    public void WhenAPluginLoadsAnotherBuildOfAnAssembly_ThenBothCopiesHaveAMismatch()
    {
        // Arrange
        LoadedAssembly[] assemblies =
        [
            new("Namotion.Interceptor", "Default", "0.5.0", CommitA, OwnRepositoryUrl),
            new("Namotion.Interceptor", "MyPlugin", "0.4.2", CommitB, OwnRepositoryUrl),
            new("HomeBlaze", "Default", "0.5.0", CommitA, OwnRepositoryUrl),
        ];

        // Act
        var inventory = AssemblyInventory.Create(assemblies, OwnRepositoryUrl);

        // Assert
        var repository = Assert.Single(inventory);
        Assert.True(repository.HasAnyMismatch);
        Assert.Equal(
            new[] { ("HomeBlaze", false), ("Namotion.Interceptor", true), ("Namotion.Interceptor (MyPlugin)", true) },
            repository.Assemblies.Select(assembly => (assembly.Key, assembly.HasMismatch)).ToArray());
    }

    [Fact]
    public void WhenTheOwnRepositoryHasTwoCommits_ThenTheRepositoryHasAMismatch()
    {
        // Arrange
        LoadedAssembly[] assemblies =
        [
            new("HomeBlaze", "Default", "0.5.0", CommitA, OwnRepositoryUrl),
            new("Namotion.Devices.Shelly", "Default", "0.5.0", CommitB, OwnRepositoryUrl),
        ];

        // Act
        var inventory = AssemblyInventory.Create(assemblies, OwnRepositoryUrl);

        // Assert
        var repository = Assert.Single(inventory);
        Assert.True(repository.HasMismatch);
        Assert.All(repository.Assemblies, assembly => Assert.False(assembly.HasMismatch));
    }

    [Fact]
    public void WhenAThirdPartyMonorepoMixesPatchVersions_ThenThereIsNoMismatch()
    {
        // Arrange
        LoadedAssembly[] assemblies =
        [
            new("Microsoft.Data.Sqlite", "Default", "10.0.12", null, "https://github.com/dotnet/dotnet"),
            new("Microsoft.Extensions.Hosting.Abstractions", "Default", "10.0.12", CommitA, "https://github.com/dotnet/dotnet"),
            new("Microsoft.Extensions.Http", "Default", "10.0.5", CommitB, "https://github.com/dotnet/dotnet"),
        ];

        // Act
        var inventory = AssemblyInventory.Create(assemblies, OwnRepositoryUrl);

        // Assert
        Assert.False(Assert.Single(inventory).HasAnyMismatch);
    }

    [Fact]
    public void WhenOneCopyHasNoCommit_ThenOnlyTheVersionDecides()
    {
        // Arrange
        LoadedAssembly[] assemblies =
        [
            new("Library", "Default", "1.0.0", null, null),
            new("Library", "MyPlugin", "1.0.0", CommitA, null),
        ];

        // Act
        var inventory = AssemblyInventory.Create(assemblies, OwnRepositoryUrl);

        // Assert
        Assert.False(Assert.Single(inventory).HasAnyMismatch);
    }

    [Fact]
    public void WhenTwoRepositoriesShareAName_ThenTheSecondGetsASuffix()
    {
        // Arrange
        LoadedAssembly[] assemblies =
        [
            new("First", "Default", "1.0.0", null, "https://github.com/first/tools"),
            new("Second", "Default", "1.0.0", null, "https://gitlab.com/second/tools"),
        ];

        // Act
        var inventory = AssemblyInventory.Create(assemblies, OwnRepositoryUrl);

        // Assert
        Assert.Equal(new[] { "tools", "tools 2" }, inventory.Select(repository => repository.Name));
    }

    [Fact]
    public void WhenTheProcessIsRead_ThenItContainsThisLibraryAndNoFrameworkAssembly()
    {
        // Act
        var assemblies = LoadedAssemblyReader.Read();

        // Assert
        Assert.Contains(assemblies, assembly => assembly.Name == "HomeBlaze.Services" && assembly.LoadContext == "Default");
        Assert.DoesNotContain(assemblies, assembly => assembly.Name == "System.Private.CoreLib");
    }

    [Fact]
    public void WhenTwoContextsShareAName_ThenTheKeysStayUnique()
    {
        // Arrange
        LoadedAssembly[] assemblies =
        [
            new("X", "P", "1.0.0", null, null),
            new("X", "P", "1.0.0", null, null),
        ];

        // Act
        var inventory = AssemblyInventory.Create(assemblies, OwnRepositoryUrl);

        // Assert
        var repository = Assert.Single(inventory);
        Assert.Equal(new[] { "X (P)", "X (P) 2" }, repository.Assemblies.Select(assembly => assembly.Key));
    }

    [Fact]
    public void WhenOneCopyHasAShortCommit_ThenThereIsNoMismatch()
    {
        // Arrange
        LoadedAssembly[] assemblies =
        [
            new("Library", "Default", "1.0.0", CommitA[..10], null),
            new("Library", "MyPlugin", "1.0.0", CommitA, null),
        ];

        // Act
        var inventory = AssemblyInventory.Create(assemblies, OwnRepositoryUrl);

        // Assert
        Assert.False(Assert.Single(inventory).HasAnyMismatch);
    }

    [Fact]
    public void WhenTheOwnRepositoryHasAShortAndAFullCommit_ThenThereIsNoMismatch()
    {
        // Arrange
        LoadedAssembly[] assemblies =
        [
            new("HomeBlaze", "Default", "0.5.0", CommitA[..10], OwnRepositoryUrl),
            new("Namotion.Devices.Shelly", "Default", "0.5.0", CommitA, OwnRepositoryUrl),
        ];

        // Act
        var inventory = AssemblyInventory.Create(assemblies, OwnRepositoryUrl);

        // Assert
        Assert.False(Assert.Single(inventory).HasMismatch);
    }

    [Fact]
    public void WhenOneOwnRepositoryAssemblyHasNoCommit_ThenThereIsNoMismatch()
    {
        // Arrange
        LoadedAssembly[] assemblies =
        [
            new("HomeBlaze", "Default", "0.5.0", CommitA, OwnRepositoryUrl),
            new("Namotion.Devices.Shelly", "Default", "0.5.0", null, OwnRepositoryUrl),
        ];

        // Act
        var inventory = AssemblyInventory.Create(assemblies, OwnRepositoryUrl);

        // Assert
        Assert.False(Assert.Single(inventory).HasMismatch);
    }

    [Fact]
    public void WhenNamesDifferOnlyByCase_ThenBothCopiesAreMarked()
    {
        // Arrange
        LoadedAssembly[] assemblies =
        [
            new("library", "Default", "1.0.0", null, null),
            new("Library", "MyPlugin", "2.0.0", null, null),
        ];

        // Act
        var inventory = AssemblyInventory.Create(assemblies, OwnRepositoryUrl);

        // Assert
        var repository = Assert.Single(inventory);
        Assert.All(repository.Assemblies, assembly => Assert.True(assembly.HasMismatch));
    }

    [Fact]
    public void WhenOneCopyHasAnEmptyVersion_ThenThereIsNoMismatch()
    {
        // Arrange
        LoadedAssembly[] assemblies =
        [
            new("Library", "Default", string.Empty, null, null),
            new("Library", "MyPlugin", "1.0.0", null, null),
        ];

        // Act
        var inventory = AssemblyInventory.Create(assemblies, OwnRepositoryUrl);

        // Assert
        Assert.False(Assert.Single(inventory).HasAnyMismatch);
    }
}
