using HomeBlaze.Storage.Internal;

namespace HomeBlaze.Storage.Tests;

public class JsonKeyComparisonTests
{
    [Fact]
    public void WhenTheReplacementHasTheSameKeysWithOtherValues_ThenNoKeyIsRemoved()
    {
        // Arrange
        var original = """{ "$type": "Device", "password": "s3cr3t", "child": { "host": "a" } }""";
        var replacement = """{ "$type": "Device", "password": "$secret:0123", "child": { "host": "b" } }""";

        // Act
        var removed = JsonKeyComparison.FindRemovedKeys(original, replacement);

        // Assert
        Assert.Empty(removed);
    }

    [Fact]
    public void WhenTheReplacementAddsKeys_ThenNoKeyIsRemoved()
    {
        // Arrange
        var original = """{ "host": "a" }""";
        var replacement = """{ "host": "a", "port": 80, "child": { "host": "b" } }""";

        // Act
        var removed = JsonKeyComparison.FindRemovedKeys(original, replacement);

        // Assert
        Assert.Empty(removed);
    }

    [Fact]
    public void WhenKeysAreMissingInTheReplacement_ThenTheirPathsAreReturned()
    {
        // Arrange
        var original = """{ "$type": "Device", "hostAdress": "a", "child": { "host": "b", "typo": 1 } }""";
        var replacement = """{ "child": { "host": "b" } }""";

        // Act
        var removed = JsonKeyComparison.FindRemovedKeys(original, replacement);

        // Assert
        Assert.Equal(["$type", "hostAdress", "child.typo"], removed);
    }

    [Fact]
    public void WhenAKeyWithANullValueIsMissingInTheReplacement_ThenItIsNotRemoved()
    {
        // Arrange
        var original = """{ "host": "a", "password": null }""";
        var replacement = """{ "host": "a" }""";

        // Act
        var removed = JsonKeyComparison.FindRemovedKeys(original, replacement);

        // Assert
        Assert.Empty(removed);
    }

    [Fact]
    public void WhenAnObjectBecomesAnotherValue_ThenItsKeysAreRemoved()
    {
        // Arrange
        var original = """{ "child": { "host": "a" } }""";
        var replacement = """{ "child": "a" }""";

        // Act
        var removed = JsonKeyComparison.FindRemovedKeys(original, replacement);

        // Assert
        Assert.Equal(["child.host"], removed);
    }

    [Fact]
    public void WhenArraysDiffer_ThenTheirElementsAreComparedUpToTheShorterLength()
    {
        // Arrange
        var original = """{ "items": [ { "name": "a", "typo": 1 }, { "name": "b", "other": 2 } ] }""";
        var replacement = """{ "items": [ { "name": "c" } ] }""";

        // Act
        var removed = JsonKeyComparison.FindRemovedKeys(original, replacement);

        // Assert
        Assert.Equal(["items[0].typo"], removed);
    }
}
