using System.Runtime.Versioning;
using HomeBlaze.Services.Secrets;

namespace HomeBlaze.Services.Tests.Secrets;

public class SecretStoreTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("homeblaze-secrets-");

    private string StorePath => Path.Combine(_directory.FullName, "Secrets.json");

    [Fact]
    public void WhenAValueIsAdded_ThenItIsReadBackByItsKey()
    {
        // Arrange
        var store = new SecretStore(StorePath);

        // Act
        var key = store.Add("hunter2");

        // Assert
        Assert.Matches("^[0-9a-f]{32}$", key);
        Assert.True(store.TryGet(key, out var value));
        Assert.Equal("hunter2", value);
    }

    [Fact]
    public void WhenTheSameValueIsAddedTwice_ThenEachAddGetsItsOwnKey()
    {
        // Arrange
        var store = new SecretStore(StorePath);

        // Act
        var first = store.Add("hunter2");
        var second = store.Add("hunter2");

        // Assert
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void WhenTheStoreIsCreatedAgain_ThenItReadsTheAddedValues()
    {
        // Arrange
        var key = new SecretStore(StorePath).Add("hunter2");

        // Act
        var store = new SecretStore(StorePath);

        // Assert
        Assert.True(store.TryGet(key, out var value));
        Assert.Equal("hunter2", value);
    }

    [Fact]
    public void WhenTheFileIsMissing_ThenTheStoreIsEmptyAndNoFileIsCreated()
    {
        // Act
        var store = new SecretStore(StorePath);

        // Assert
        Assert.False(store.TryGet("0123456789abcdef0123456789abcdef", out _));
        Assert.False(File.Exists(StorePath));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("""{ "key": 1 }""")]
    [InlineData("""{ "key": null }""")]
    [InlineData("null")]
    public void WhenTheFileIsNotAnObjectOfStrings_ThenTheConstructorThrowsAndTheFileStays(string content)
    {
        // Arrange
        File.WriteAllText(StorePath, content);

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() => new SecretStore(StorePath));
        Assert.Contains(StorePath, exception.Message);
        Assert.Equal(content, File.ReadAllText(StorePath));
    }

    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public void WhenAValueIsAdded_ThenOnlyTheOwnerCanReadTheFile()
    {
        // Arrange
        var store = new SecretStore(StorePath);

        // Act
        store.Add("hunter2");

        // Assert
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(StorePath));
    }

    public void Dispose() => _directory.Delete(recursive: true);
}
