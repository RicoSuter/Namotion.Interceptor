using HomeBlaze.Components;
using HomeBlaze.Services;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Internal;
using HomeBlaze.Storage.Pages;
using HomeBlaze.Storage.Tests.Pages;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using static HomeBlaze.Storage.Tests.Pages.TestTypeNames;

namespace HomeBlaze.Storage.Tests;

public class MarkdownSlotContentWriteTests : IDisposable
{
    private const string CardPage = "# Cards\n\n:: HomeBlaze.Components.Card#card Title=A\nBody.\n::\n";

    private static readonly string CardWithFooterPage =
        $"# Cards\n\n:: HomeBlaze.Components.Card#card Title=A\nBody.\n\n::: {LeafType}#bodyleaf Name=b\n:::\n::: Footer\n:::: {LeafType}#footerleaf Name=f\n::::\n:::\n::\n";

    private const string AlertPage = "# Alerts\n\n:: HomeBlaze.Components.Alert#alert\n::\n";

    private static readonly string FlowPage =
        $"# Flow\n\n:: HomeBlaze.Components.FlowLayout#flow\n::: Item\n:::: {LeafType}#first Name=a\n::::\n:::\n::\n";

    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("homeblaze-slot-content-");
    private readonly List<FluentStorageContainer> _storages = [];

    [Fact]
    public async Task WhenTextIsAddedToAnEmptyDefaultBody_ThenItIsTheBodyAndTheBlockKeepsItsSubject()
    {
        // Arrange
        var markdown = await LoadAsync(AlertPage);
        var alert = Assert.IsType<Alert>(markdown.Children["alert"]);
        Assert.Null(alert.Content);

        // Act
        var result = await markdown.AddTextToSlotAsync(alert, null, "Added.", CancellationToken.None);

        // Assert
        Assert.True(result.IsWritten);
        Assert.Equal("# Alerts\n\n:: HomeBlaze.Components.Alert#alert\n\nAdded.\n\n::\n", ReadPage());
        Assert.Same(alert, markdown.Children["alert"]);
        var content = Assert.IsType<MarkdownFragment>(alert.Content);
        var written = Assert.IsType<HtmlSegment>(result.Subject);
        Assert.Contains("Added.", written.Html);
        Assert.Contains(written, content.Children.Values);
    }

    [Fact]
    public async Task WhenAWidgetIsAddedToAnEmptyDefaultBody_ThenItIsTheBodyAndTheResultNamesIt()
    {
        // Arrange
        var markdown = await LoadAsync(AlertPage);
        var alert = markdown.Children["alert"];

        // Act
        var result = await markdown.AddWidgetToSlotAsync(alert, null, new TestLeaf { Name = "fresh" }, "added", CancellationToken.None);

        // Assert
        Assert.True(result.IsWritten);
        Assert.Equal($"# Alerts\n\n:: HomeBlaze.Components.Alert#alert\n\n::: {LeafType}#added Name=fresh\n:::\n\n::\n", ReadPage());
        Assert.Same(alert, markdown.Children["alert"]);
        var leaf = Assert.IsType<TestLeaf>(result.Subject);
        Assert.Equal("fresh", leaf.Name);
        Assert.Contains(leaf, Assert.IsType<MarkdownFragment>(((Alert)alert).Content).Children.Values);
    }

    [Fact]
    public async Task WhenTextAndAWidgetAreAddedToADefaultBody_ThenTheyFollowTheBody()
    {
        // Arrange
        var markdown = await LoadAsync(CardPage);
        var card = markdown.Children["card"];

        // Act
        var textResult = await markdown.AddTextToSlotAsync(card, null, "Added.", CancellationToken.None);
        var widgetResult = await markdown.AddWidgetToSlotAsync(card, null, new TestLeaf { Name = "fresh" }, null, CancellationToken.None);

        // Assert
        Assert.True(textResult.IsWritten);
        Assert.True(widgetResult.IsWritten);
        Assert.Equal(
            $"# Cards\n\n:: HomeBlaze.Components.Card#card Title=A\nBody.\n\nAdded.\n\n::: {LeafType} Name=fresh\n:::\n\n::\n",
            ReadPage());
        Assert.Same(card, markdown.Children["card"]);
    }

    [Fact]
    public async Task WhenAWidgetIsAddedToADefaultBodyBeforeASlotBlock_ThenItGoesBeforeTheSlotBlock()
    {
        // Arrange
        var markdown = await LoadAsync(CardWithFooterPage);
        var card = Assert.IsType<Card>(markdown.Children["card"]);
        var bodyLeaf = ((MarkdownFragment)card.Content!).Children.Values.OfType<TestLeaf>().Single();

        // Act
        var result = await markdown.AddWidgetToSlotAsync(card, null, new TestLeaf { Name = "fresh" }, "added", CancellationToken.None);

        // Assert
        Assert.True(result.IsWritten);
        Assert.Contains(
            $"::: {LeafType}#bodyleaf Name=b\n:::\n\n::: {LeafType}#added Name=fresh\n:::\n\n::: Footer\n", ReadPage());
        var leaves = ((MarkdownFragment)card.Content!).Children.Values.OfType<TestLeaf>().ToList();
        Assert.Same(bodyLeaf, leaves[0]);
        Assert.Same(leaves[1], result.Subject);
        Assert.Same(card, markdown.Children["card"]);
    }

    [Fact]
    public async Task WhenTextIsAddedToAnExistingNamedSlot_ThenItFollowsTheContentOfTheSlotBlock()
    {
        // Arrange
        var markdown = await LoadAsync(CardWithFooterPage);
        var card = Assert.IsType<Card>(markdown.Children["card"]);
        var footer = card.Footer;
        var footerLeaf = ((MarkdownFragment)footer!).Children.Values.OfType<TestLeaf>().Single();

        // Act
        var result = await markdown.AddTextToSlotAsync(card, "Footer", "Foot.", CancellationToken.None);

        // Assert
        Assert.True(result.IsWritten);
        Assert.Contains($"::: Footer\n:::: {LeafType}#footerleaf Name=f\n::::\n\nFoot.\n\n:::\n::\n", ReadPage());
        Assert.Same(footer, card.Footer);
        Assert.Same(footerLeaf, ((MarkdownFragment)card.Footer!).Children.Values.OfType<TestLeaf>().Single());
        Assert.Contains(Assert.IsType<HtmlSegment>(result.Subject), ((MarkdownFragment)card.Footer!).Children.Values);
    }

    [Fact]
    public async Task WhenAMissingNamedSlotIsDeclaredBeforeASlotThatHasABlock_ThenItsNewBlockGoesBeforeThatBlock()
    {
        // Arrange
        var markdown = await LoadAsync(CardWithFooterPage);
        var card = Assert.IsType<Card>(markdown.Children["card"]);
        var footer = card.Footer;

        // Act
        var result = await markdown.AddTextToSlotAsync(card, "HeaderRight", "Right.", CancellationToken.None);

        // Assert
        Assert.True(result.IsWritten);
        Assert.Contains($":::\n::: HeaderRight\n\nRight.\n\n:::\n::: Footer\n:::: {LeafType}#footerleaf", ReadPage());
        Assert.Same(footer, card.Footer);
        Assert.Contains(Assert.IsType<HtmlSegment>(result.Subject), Assert.IsType<MarkdownFragment>(card.HeaderRight).Children.Values);
    }

    [Fact]
    public async Task WhenAWidgetOfThePageIsAddedToAMissingNamedSlot_ThenItsFenceIsRaisedToStandInTheNewSlotBlock()
    {
        // Arrange
        var markdown = await LoadAsync(CardPage);
        var card = Assert.IsType<Card>(markdown.Children["card"]);

        // Act
        var result = await markdown.AddTextToSlotAsync(card, "Footer", $":: {LeafType}#footleaf Name=f\n::", CancellationToken.None);

        // Assert
        Assert.True(result.IsWritten);
        Assert.Contains($"::: Footer\n\n:::: {LeafType}#footleaf Name=f\n::::\n\n:::\n::\n", ReadPage());
        Assert.Same(result.Subject, ((MarkdownFragment)card.Footer!).Children.Values.OfType<TestLeaf>().Single());
    }

    [Fact]
    public async Task WhenTheContentForAMissingNamedSlotIsRefused_ThenNothingIsWritten()
    {
        // Arrange
        const string source = $"# Cards\n\n:: HomeBlaze.Components.Card#card Title=A\nBody.\n\n::: {LeafType}#bodyleaf Name=b\n:::\n::\n";
        var markdown = await LoadAsync(source);
        var card = Assert.IsType<Card>(markdown.Children["card"]);
        var body = card.Content;

        // Act
        var result = await markdown.AddTextToSlotAsync(card, "Footer", $"Foot.\n\n:::: {LeafType}#bodyleaf\n::::", CancellationToken.None);

        // Assert
        Assert.Equal("The name 'bodyleaf' is already used on this page.", result.Refusal);
        Assert.Equal(source, ReadPage());
        Assert.Equal(source, markdown.Content);
        Assert.Same(body, card.Content);
        Assert.Null(card.Footer);
    }

    [Fact]
    public async Task WhenContentIsAddedToADefaultBodyBeforeASlotBlock_ThenItGoesBeforeTheSlotBlock()
    {
        // Arrange
        var markdown = await LoadAsync(CardWithFooterPage);
        var card = Assert.IsType<Card>(markdown.Children["card"]);
        var bodyLeaf = ((MarkdownFragment)card.Content!).Children.Values.OfType<TestLeaf>().Single();
        var footerLeaf = ((MarkdownFragment)card.Footer!).Children.Values.OfType<TestLeaf>().Single();

        // Act
        var result = await markdown.AddTextToSlotAsync(card, null, "Added.", CancellationToken.None);

        // Assert
        Assert.True(result.IsWritten);
        Assert.Contains($"::: {LeafType}#bodyleaf Name=b\n:::\n\nAdded.\n\n::: Footer\n", ReadPage());
        Assert.Same(card, markdown.Children["card"]);
        Assert.Same(bodyLeaf, ((MarkdownFragment)card.Content!).Children.Values.OfType<TestLeaf>().Single());
        Assert.Same(footerLeaf, ((MarkdownFragment)card.Footer!).Children.Values.OfType<TestLeaf>().Single());
    }

    [Fact]
    public async Task WhenTextIsAddedToAMissingNamedSlot_ThenANewSlotBlockHoldsIt()
    {
        // Arrange
        var markdown = await LoadAsync(CardPage);
        var card = Assert.IsType<Card>(markdown.Children["card"]);
        var body = card.Content;

        // Act
        var result = await markdown.AddTextToSlotAsync(card, "Footer", "Foot.", CancellationToken.None);

        // Assert
        Assert.True(result.IsWritten);
        Assert.Equal("# Cards\n\n:: HomeBlaze.Components.Card#card Title=A\nBody.\n::: Footer\n\nFoot.\n\n:::\n::\n", ReadPage());
        Assert.Same(card, markdown.Children["card"]);
        Assert.Same(body, card.Content);
        var written = Assert.IsType<HtmlSegment>(result.Subject);
        Assert.Contains("Foot.", written.Html);
        Assert.Contains(written, Assert.IsType<MarkdownFragment>(card.Footer).Children.Values);
    }

    [Fact]
    public async Task WhenAWidgetIsAddedToAMissingNamedSlot_ThenANewSlotBlockHoldsIt()
    {
        // Arrange
        var markdown = await LoadAsync(CardPage);
        var card = Assert.IsType<Card>(markdown.Children["card"]);

        // Act
        var result = await markdown.AddWidgetToSlotAsync(card, "HeaderRight", new TestLeaf { Name = "fresh" }, "added", CancellationToken.None);

        // Assert
        Assert.True(result.IsWritten);
        Assert.Equal(
            $"# Cards\n\n:: HomeBlaze.Components.Card#card Title=A\nBody.\n::: HeaderRight\n\n:::: {LeafType}#added Name=fresh\n::::\n\n:::\n::\n",
            ReadPage());
        Assert.Same(card, markdown.Children["card"]);
        var leaf = Assert.IsType<TestLeaf>(result.Subject);
        Assert.Contains(leaf, Assert.IsType<MarkdownFragment>(card.HeaderRight).Children.Values);
    }

    [Fact]
    public async Task WhenContentIsAddedToAnExistingNamedSlot_ThenItIsWrittenAsAnInsertAtTheEndOfItsContent()
    {
        // Arrange
        var markdown = await LoadAsync(CardWithFooterPage, "Added.md");
        var inserted = await LoadAsync(CardWithFooterPage, "Inserted.md");
        var card = Assert.IsType<Card>(markdown.Children["card"]);
        var footerLeaf = ((MarkdownFragment)card.Footer!).Children.Values.OfType<TestLeaf>().Single();
        var insertedCard = Assert.IsType<Card>(inserted.Children["card"]);

        // Act
        var result = await markdown.AddWidgetToSlotAsync(card, "Footer", new TestLeaf { Name = "fresh" }, null, CancellationToken.None);
        await inserted.InsertWidgetAsync(insertedCard.Footer!, null, new TestLeaf { Name = "fresh" }, null, CancellationToken.None);

        // Assert
        Assert.True(result.IsWritten);
        Assert.Contains($"::: Footer\n:::: {LeafType}#footerleaf Name=f\n::::\n\n:::: {LeafType} Name=fresh\n::::\n\n:::\n", ReadPage("Added.md"));
        Assert.Equal(ReadPage("Inserted.md"), ReadPage("Added.md"));
        var leaves = ((MarkdownFragment)card.Footer!).Children.Values.OfType<TestLeaf>().ToList();
        Assert.Same(footerLeaf, leaves[0]);
        Assert.Same(leaves[1], result.Subject);
    }

    [Fact]
    public async Task WhenTextIsAddedToADefaultBody_ThenItIsWrittenAsAnInsertAtTheEndOfTheBody()
    {
        // Arrange
        var markdown = await LoadAsync(CardWithFooterPage, "Added.md");
        var inserted = await LoadAsync(CardWithFooterPage, "Inserted.md");
        var insertedCard = Assert.IsType<Card>(inserted.Children["card"]);

        // Act
        await markdown.AddTextToSlotAsync(markdown.Children["card"], null, "Added.", CancellationToken.None);
        await inserted.InsertTextAsync(insertedCard.Content!, null, "Added.", CancellationToken.None);

        // Assert
        Assert.Equal(ReadPage("Inserted.md"), ReadPage("Added.md"));
    }

    [Fact]
    public async Task WhenContentIsAddedToAListSlotOfContentItems_ThenANewItemHoldsItAndTheOtherItemsStay()
    {
        // Arrange
        var markdown = await LoadAsync(FlowPage);
        var flow = Assert.IsType<FlowLayout>(markdown.Children["flow"]);
        var firstItem = flow.Items[0];
        var firstLeaf = ((MarkdownFragment)firstItem).Children.Values.OfType<TestLeaf>().Single();

        // Act
        var widgetResult = await markdown.AddWidgetToSlotAsync(flow, "Item", new TestLeaf { Name = "fresh" }, null, CancellationToken.None);
        var textResult = await markdown.AddTextToSlotAsync(flow, "Item", "Third.", CancellationToken.None);

        // Assert
        Assert.True(widgetResult.IsWritten);
        Assert.True(textResult.IsWritten);
        Assert.Equal(
            $"# Flow\n\n:: HomeBlaze.Components.FlowLayout#flow\n::: Item\n:::: {LeafType}#first Name=a\n::::\n:::\n" +
            $"::: Item\n\n:::: {LeafType} Name=fresh\n::::\n\n:::\n::: Item\n\nThird.\n\n:::\n::\n",
            ReadPage());
        Assert.Same(flow, markdown.Children["flow"]);
        Assert.Equal(3, flow.Items.Length);
        Assert.Same(firstItem, flow.Items[0]);
        Assert.Same(firstLeaf, ((MarkdownFragment)flow.Items[0]).Children.Values.OfType<TestLeaf>().Single());
        Assert.Contains(widgetResult.Subject!, ((MarkdownFragment)flow.Items[1]).Children.Values);
        Assert.Contains(textResult.Subject!, ((MarkdownFragment)flow.Items[2]).Children.Values);
    }

    [Theory]
    [InlineData("Tab", "The block has no slot 'Tab' for content.")]
    [InlineData("Unknown", "The block has no slot 'Unknown' for content.")]
    [InlineData(null, "The block has no default slot for content.")]
    public async Task WhenTheSlotTakesNoContent_ThenTheAddIsRefusedAndTheFileIsUnchanged(string? slotName, string refusal)
    {
        // Arrange
        const string source = "# Tabs\n\n:: HomeBlaze.Components.TabLayout#tabs\n::: Tab Title=X\nIn the tab.\n:::\n::\n";
        var markdown = await LoadAsync(source);

        // Act
        var result = await markdown.AddTextToSlotAsync(markdown.Children["tabs"], slotName, "Added.", CancellationToken.None);

        // Assert
        Assert.Equal(refusal, result.Refusal);
        Assert.Equal(source, ReadPage());
    }

    [Fact]
    public async Task WhenAnItemIsAddedToAListSlotOfContentItems_ThenTheAddIsRefusedAndThePageIsUnchanged()
    {
        // Arrange: the items of a flow hold page content, which AddWidgetToSlotAsync and AddTextToSlotAsync add.
        var markdown = await LoadAsync(FlowPage);
        var flow = markdown.Children["flow"];

        // Act
        var result = await markdown.AddSlotItemAsync(flow, "Item", new TestLeaf { Name = "item" }, CancellationToken.None);

        // Assert
        Assert.Equal("The block has no list slot 'Item' for this item.", result.Refusal);
        Assert.Equal(FlowPage, ReadPage());
    }

    [Fact]
    public async Task WhenTheBlockHasNoClosingLine_ThenTheAddIsRefusedAndTheFileIsUnchanged()
    {
        // Arrange
        const string source = "# Cards\n\n:: HomeBlaze.Components.Card#card Title=A\nBody.\n";
        var markdown = await LoadAsync(source);

        // Act
        var result = await markdown.AddWidgetToSlotAsync(markdown.Children["card"], "Footer", new TestLeaf(), null, CancellationToken.None);

        // Assert
        Assert.Equal("Add the closing line of the block first.", result.Refusal);
        Assert.Equal(source, ReadPage());
    }

    [Fact]
    public async Task WhenTheFileChangedSinceThePageWasRead_ThenTheAddIsRefusedAndThePageReadsTheFile()
    {
        // Arrange
        var markdown = await LoadAsync(CardPage);
        var changed = CardPage + "\nexternal\n";
        WriteFile("Page.md", changed);

        // Act
        var result = await markdown.AddTextToSlotAsync(markdown.Children["card"], "Footer", "Foot.", CancellationToken.None);

        // Assert
        Assert.Equal("The page changed while it was edited. Try again.", result.Refusal);
        Assert.Equal(changed, ReadPage());
        Assert.Equal(changed, markdown.Content);
    }

    [Fact]
    public async Task WhenTheContentSlotsOfBlocksAreAskedFor_ThenTheyAreTheSlotsThatTakeContentWithWhetherTheyHoldAny()
    {
        // Arrange
        var markdown = await LoadAsync(
            CardPage + "\n" + FlowPage + "\n" + AlertPage +
            "\n:: HomeBlaze.Components.TabLayout#tabs\n::: Tab Title=X\n:::\n::\n" +
            "\n:: HomeBlaze.Components.Card#open Title=B\nOpen body.\n");

        // Act & Assert
        Assert.Equal(
            [
                new MarkdownContentSlot(null, MarkdownContentSlotKind.Default, true),
                new MarkdownContentSlot("HeaderRight", MarkdownContentSlotKind.Named, false),
                new MarkdownContentSlot("Footer", MarkdownContentSlotKind.Named, false)
            ],
            markdown.GetContentSlots(markdown.Children["card"]));
        Assert.Equal([new MarkdownContentSlot("Item", MarkdownContentSlotKind.ListItem, true)], markdown.GetContentSlots(markdown.Children["flow"]));
        Assert.Equal([new MarkdownContentSlot(null, MarkdownContentSlotKind.Default, false)], markdown.GetContentSlots(markdown.Children["alert"]));
        Assert.Empty(markdown.GetContentSlots(markdown.Children["tabs"]));
        Assert.Empty(markdown.GetContentSlots(markdown.Children["open"]));
        Assert.Empty(markdown.GetContentSlots(new Card()));
    }

    [Fact]
    public async Task WhenTheSlotsOfWidgetsThatShipAreAskedFor_ThenTheContentSlotsAndTheSlotsOfConfiguredItemsAreApart()
    {
        // Arrange
        var markdown = await LoadAsync(
            CardPage + "\n" + FlowPage + "\n" + AlertPage +
            "\n:: HomeBlaze.Components.TabLayout#tabs\n::: Tab Title=X\n:::\n::\n" +
            "\n:: HomeBlaze.Components.Switch#switch\n::\n");
        var card = markdown.Children["card"];
        var flow = markdown.Children["flow"];
        var tabs = markdown.Children["tabs"];
        var alert = markdown.Children["alert"];
        var toggle = markdown.Children["switch"];

        // Act & Assert
        Assert.Empty(markdown.GetListSlots(card));
        Assert.Empty(markdown.GetListSlots(flow));
        Assert.Equal([new MarkdownListSlot("Tab", typeof(Tab))], markdown.GetListSlots(tabs));
        Assert.Empty(markdown.GetListSlots(alert));
        Assert.Empty(markdown.GetListSlots(toggle));
        Assert.Equal([new MarkdownContentSlot("Badge", MarkdownContentSlotKind.Named, false)], markdown.GetContentSlots(toggle));
    }

    private async Task<MarkdownFile> LoadAsync(string content, string name = "Page.md")
    {
        WriteFile(name, content);
        var storage = await ConnectAsync();
        return Assert.IsType<MarkdownFile>(storage.Children[name]);
    }

    private async Task<FluentStorageContainer> ConnectAsync()
    {
        var typeProvider = new TypeProvider();
        typeProvider.AddAssembly(typeof(FluentStorageContainer).Assembly);
        typeProvider.AddAssembly(typeof(Card).Assembly);
        typeProvider.AddAssembly(typeof(MarkdownSlotContentWriteTests).Assembly);

        var services = new ServiceCollection();
        var context = SubjectContextFactory.Create(services);
        services.AddSingleton(typeProvider);
        services.AddSingleton(new SubjectTypeRegistry(typeProvider));
        services.AddSingleton(context);
        services.AddSingleton<SubjectFactory>();
        services.AddSingleton<ConfigurableSubjectSerializer>();
        services.AddSingleton<RootManager>();
        services.AddSingleton(provider => new SubjectPathResolver(() => provider.GetRequiredService<RootManager>().Root));
        services.AddSingleton<MarkdownContentParser>();
        var serviceProvider = services.BuildServiceProvider();

        var storage = new FluentStorageContainer(
            serviceProvider.GetRequiredService<SubjectTypeRegistry>(),
            serviceProvider.GetRequiredService<ConfigurableSubjectSerializer>(),
            serviceProvider)
        {
            StorageType = "disk",
            ConnectionString = _directory.FullName,
            EnableFileWatching = false
        };

        ((IInterceptorSubject)storage).Context.AddFallbackContext(context);
        _storages.Add(storage);
        await storage.ConnectAsync(CancellationToken.None);
        return storage;
    }

    private void WriteFile(string name, string content) => File.WriteAllText(Path.Combine(_directory.FullName, name), content);

    private string ReadPage(string name = "Page.md") => File.ReadAllText(Path.Combine(_directory.FullName, name));

    public void Dispose()
    {
        foreach (var storage in _storages)
        {
            storage.Dispose();
        }

        _directory.Delete(recursive: true);
    }
}
