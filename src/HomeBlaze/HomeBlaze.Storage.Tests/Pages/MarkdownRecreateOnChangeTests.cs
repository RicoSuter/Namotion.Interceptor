namespace HomeBlaze.Storage.Tests.Pages;

public class MarkdownRecreateOnChangeTests
{
    private static readonly string LeafType = typeof(RecreatingTestLeaf).FullName!;

    [Fact]
    public async Task WhenARecreateOnChangeValueOfABlockChanges_ThenANewInstanceIsCreated()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", $":: {LeafType}#leaf Name=a Shape=round\n::\n");
        var leaf = (RecreatingTestLeaf)page.Children["leaf"];

        // Act
        await fixture.UpdatePageAsync(page, $":: {LeafType}#leaf Name=a Shape=square\n::\n");

        // Assert
        var reloaded = Assert.IsType<RecreatingTestLeaf>(page.Children["leaf"]);
        Assert.NotSame(leaf, reloaded);
        Assert.Equal("square", reloaded.Shape);
    }

    [Fact]
    public async Task WhenAnOrdinaryValueOfABlockChanges_ThenTheInstanceIsKept()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", $":: {LeafType}#leaf Name=a Shape=round\n::\n");
        var leaf = (RecreatingTestLeaf)page.Children["leaf"];

        // Act
        await fixture.UpdatePageAsync(page, $":: {LeafType}#leaf Name=bb Shape=round\n::\n");

        // Assert
        Assert.Same(leaf, page.Children["leaf"]);
        Assert.Equal("bb", leaf.Name);
    }
}
