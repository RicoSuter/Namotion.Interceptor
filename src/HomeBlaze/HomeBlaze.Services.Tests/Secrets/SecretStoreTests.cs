using System.Runtime.Versioning;
using HomeBlaze.Services.Secrets;

namespace HomeBlaze.Services.Tests.Secrets;

public class SecretStoreTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("homeblaze-secrets-");

    private string StorePath => Path.Combine(_directory.FullName, "Secrets.json");

    [Fact]
    public void WhenKeysAreCreated_ThenEachIsItsOwnThirtyTwoLowercaseHexDigits()
    {
        // Act
        var first = SecretStore.CreateKey();
        var second = SecretStore.CreateKey();

        // Assert
        Assert.Matches("^[0-9a-f]{32}$", first);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void WhenAValueIsSetUnderAnExistingKey_ThenItReplacesTheValueAndKeepsTheOthers()
    {
        // Arrange
        var store = new SecretStore(StorePath);
        const string key = "0123456789abcdef0123456789abcdef";
        const string otherKey = "fedcba9876543210fedcba9876543210";
        store.Set(key, "hunter2");
        store.Set(otherKey, "other");

        // Act
        store.Set(key, "correct horse");

        // Assert
        Assert.True(store.TryGet(key, out var value));
        Assert.Equal("correct horse", value);
        Assert.True(store.TryGet(otherKey, out var otherValue));
        Assert.Equal("other", otherValue);
    }

    [Fact]
    public void WhenAValueIsSetUnderAMissingKey_ThenItIsAddedUnderThatKey()
    {
        // Arrange
        var store = new SecretStore(StorePath);
        const string key = "0123456789abcdef0123456789abcdef";

        // Act
        store.Set(key, "hunter2");

        // Assert
        Assert.True(store.TryGet(key, out var value));
        Assert.Equal("hunter2", value);
    }

    [Fact]
    public void WhenTheStoreIsCreatedAgain_ThenItReadsTheSetValues()
    {
        // Arrange
        var first = new SecretStore(StorePath);
        const string key = "0123456789abcdef0123456789abcdef";
        first.Set(key, "hunter2");
        first.Set(key, "correct horse");

        // Act
        var store = new SecretStore(StorePath);

        // Assert
        Assert.True(store.TryGet(key, out var value));
        Assert.Equal("correct horse", value);
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
    public void WhenAValueIsSet_ThenOnlyTheOwnerCanReadTheFile()
    {
        // Arrange
        var store = new SecretStore(StorePath);

        // Act
        store.Set("0123456789abcdef0123456789abcdef", "hunter2");

        // Assert
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(StorePath));
    }

    public void Dispose() => _directory.Delete(recursive: true);
}
