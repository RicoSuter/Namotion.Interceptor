namespace HomeBlaze.Storage.Tests;

public class StorageRecreateOnChangeTests : StorageTestBase
{
    private static readonly string LeafType = typeof(RecreatingTestLeaf).FullName!;

    [Fact]
    public async Task WhenAKeyIsRemovedFromAJsonSubjectFile_ThenANewInstanceIsCreated()
    {
        // Arrange
        WriteFile("Leaf.json", $$"""{ "$type": "{{LeafType}}", "name": "a", "shape": "round" }""");
        var storage = await ConnectAsync();
        var leaf = Assert.IsType<RecreatingTestLeaf>(storage.Children["Leaf"]);
        WriteFile("Leaf.json", $$"""{ "$type": "{{LeafType}}", "shape": "round" }""");

        // Act
        await storage.ReconcileAsync();

        // Assert
        var reloaded = Assert.IsType<RecreatingTestLeaf>(storage.Children["Leaf"]);
        Assert.NotSame(leaf, reloaded);
        Assert.Equal(string.Empty, reloaded.Name);
    }

    [Fact]
    public async Task WhenAnOrdinaryValueOfAJsonSubjectFileChanges_ThenTheInstanceIsUpdatedInPlace()
    {
        // Arrange
        WriteFile("Leaf.json", $$"""{ "$type": "{{LeafType}}", "name": "a", "shape": "round" }""");
        var storage = await ConnectAsync();
        var leaf = Assert.IsType<RecreatingTestLeaf>(storage.Children["Leaf"]);
        WriteFile("Leaf.json", $$"""{ "$type": "{{LeafType}}", "name": "bbb", "shape": "round" }""");

        // Act
        await storage.ReconcileAsync();

        // Assert
        Assert.Same(leaf, storage.Children["Leaf"]);
        Assert.Equal("bbb", leaf.Name);
    }

    [Fact]
    public async Task WhenARecreateOnChangeValueOfAJsonSubjectFileChanges_ThenANewInstanceIsCreated()
    {
        // Arrange
        WriteFile("Leaf.json", $$"""{ "$type": "{{LeafType}}", "name": "a", "shape": "round" }""");
        var storage = await ConnectAsync();
        var leaf = Assert.IsType<RecreatingTestLeaf>(storage.Children["Leaf"]);
        WriteFile("Leaf.json", $$"""{ "$type": "{{LeafType}}", "name": "a", "shape": "square" }""");

        // Act
        await storage.ReconcileAsync();

        // Assert
        var reloaded = Assert.IsType<RecreatingTestLeaf>(storage.Children["Leaf"]);
        Assert.NotSame(leaf, reloaded);
        Assert.Equal("square", reloaded.Shape);
    }
}
