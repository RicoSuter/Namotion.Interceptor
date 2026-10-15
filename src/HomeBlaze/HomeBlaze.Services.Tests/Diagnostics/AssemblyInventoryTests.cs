using HomeBlaze.Services.Diagnostics;
using Xunit;

namespace HomeBlaze.Services.Tests.Diagnostics;

public class AssemblyInventoryTests
{
    private const string OwnRepositoryUrl = "https://github.com/RicoSuter/Namotion.Interceptor";
    private const string Commit = "b1b7c2f4568b7a83baf8d041afaab8f2214fb6cd";

    [Fact]
    public void WhenAssembliesComeFromRepositories_ThenTheyAreGroupedAndUnknownIsLast()
    {
        // Arrange
        LoadedAssembly[] assemblies =
        [
            new("SomeLibrary", "Default", "1.2.0", null, null),
            new("Namotion.Interceptor", "Default", "0.5.0", Commit, OwnRepositoryUrl),
            new("HomeBlaze", "Default", "0.5.0", Commit, OwnRepositoryUrl),
            new("MudBlazor", "Default", "9.2.0", null, "https://github.com/MudBlazor/MudBlazor"),
        ];

        // Act
        var inventory = AssemblyInventory.Create(assemblies);

        // Assert
        Assert.Equal(new[] { "MudBlazor", "Namotion.Interceptor", "Unknown" }, inventory.Select(repository => repository.Name));
        Assert.Equal(new[] { "HomeBlaze", "Namotion.Interceptor" }, inventory[1].Assemblies.Select(assembly => assembly.Name));
        Assert.Null(inventory[2].RepositoryUrl);
    }

    [Fact]
    public void WhenAPluginLoadsAnotherCopyOfAnAssembly_ThenTheDefaultCopyComesFirstAndThePluginCopyNamesItsContext()
    {
        // Arrange
        LoadedAssembly[] assemblies =
        [
            new("Namotion.Interceptor", "MyPlugin", "0.4.2", null, OwnRepositoryUrl),
            new("Namotion.Interceptor", "Default", "0.5.0", Commit, OwnRepositoryUrl),
        ];

        // Act
        var inventory = AssemblyInventory.Create(assemblies);

        // Assert
        var repository = Assert.Single(inventory);
        Assert.Equal(
            new[] { ("Namotion.Interceptor", "0.5.0"), ("Namotion.Interceptor (MyPlugin)", "0.4.2") },
            repository.Assemblies.Select(assembly => (assembly.Key, assembly.Version)).ToArray());
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
        var inventory = AssemblyInventory.Create(assemblies);

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
        var inventory = AssemblyInventory.Create(assemblies);

        // Assert
        var repository = Assert.Single(inventory);
        Assert.Equal(new[] { "X (P)", "X (P) 2" }, repository.Assemblies.Select(assembly => assembly.Key));
    }
}
