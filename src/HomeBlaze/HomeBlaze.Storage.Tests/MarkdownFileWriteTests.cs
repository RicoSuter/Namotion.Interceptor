using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Services;
using HomeBlaze.Storage.Abstractions;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Internal;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;

namespace HomeBlaze.Storage.Tests;

public class MarkdownFileWriteTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("homeblaze-markdown-");
    private readonly List<FluentStorageContainer> _storages = [];

    [Fact]
    public async Task WhenReplacingTheContent_ThenTheFileTheContentAndTheParsedChildrenAreUpdated()
    {
        // Arrange
        WriteFile("Notes.md", "# Old heading");
        var storage = await ConnectAsync();
        var markdown = Assert.IsType<MarkdownFile>(storage.Children["Notes.md"]);

        // Act
        await markdown.ReplaceContentAsync("# New heading\n\nBody", CancellationToken.None);

        // Assert
        Assert.Equal("# New heading\n\nBody", ReadFile("Notes.md"));
        Assert.Equal("# New heading\n\nBody", markdown.Content);
        Assert.Contains(markdown.Children.Values.OfType<HtmlSegment>(), segment => segment.Html.Contains("New heading"));
        Assert.DoesNotContain(markdown.Children.Values.OfType<HtmlSegment>(), segment => segment.Html.Contains("Old heading"));
    }

    [Fact]
    public async Task WhenTheNewContentExceedsTheSizeLimit_ThenItIsRejectedAndTheFileIsUnchanged()
    {
        // Arrange
        WriteFile("Notes.md", "# Heading");
        var storage = await ConnectAsync();
        var markdown = Assert.IsType<MarkdownFile>(storage.Children["Notes.md"]);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => markdown.ReplaceContentAsync(new string('a', MarkdownFile.MaxContentSize + 1), CancellationToken.None));
        Assert.Contains("65536", exception.Message);
        Assert.Equal("# Heading", ReadFile("Notes.md"));
    }

    [Fact]
    public async Task WhenTheNewContentIsExactlyAtTheSizeLimit_ThenItIsWritten()
    {
        // Arrange
        WriteFile("Notes.md", "# Heading");
        var storage = await ConnectAsync();
        var markdown = Assert.IsType<MarkdownFile>(storage.Children["Notes.md"]);
        var content = new string('a', MarkdownFile.MaxContentSize);

        // Act
        await markdown.ReplaceContentAsync(content, CancellationToken.None);

        // Assert
        Assert.Equal(content, ReadFile("Notes.md"));
    }

    [Fact]
    public async Task WhenReplacingAUniquePassage_ThenOnlyThatPassageChanges()
    {
        // Arrange
        WriteFile("Notes.md", "# Heading\n\nThe freezer is at {{ /Kitchen/Freezer/Temperature }}.\n\nEnd");
        var storage = await ConnectAsync();
        var markdown = Assert.IsType<MarkdownFile>(storage.Children["Notes.md"]);

        // Act
        await markdown.ReplaceTextAsync("The freezer is at", "The kitchen freezer is at", CancellationToken.None);

        // Assert
        var expected = "# Heading\n\nThe kitchen freezer is at {{ /Kitchen/Freezer/Temperature }}.\n\nEnd";
        Assert.Equal(expected, ReadFile("Notes.md"));
        Assert.Equal(expected, markdown.Content);
    }

    [Fact]
    public async Task WhenThePassageIsNotFound_ThenItIsRejectedAndTheFileIsUnchanged()
    {
        // Arrange
        WriteFile("Notes.md", "# Heading");
        var storage = await ConnectAsync();
        var markdown = Assert.IsType<MarkdownFile>(storage.Children["Notes.md"]);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => markdown.ReplaceTextAsync("# Other", "# New", CancellationToken.None));
        Assert.Contains("was not found", exception.Message);
        Assert.Equal("# Heading", ReadFile("Notes.md"));
    }

    [Theory]
    [InlineData("one two one", "one")]
    [InlineData("aaa", "aa")]
    public async Task WhenThePassageOccursMoreThanOnce_ThenItIsRejectedAndTheFileIsUnchanged(string content, string oldText)
    {
        // Arrange
        WriteFile("Notes.md", content);
        var storage = await ConnectAsync();
        var markdown = Assert.IsType<MarkdownFile>(storage.Children["Notes.md"]);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => markdown.ReplaceTextAsync(oldText, "x", CancellationToken.None));
        Assert.Contains("more than once", exception.Message);
        Assert.Equal(content, ReadFile("Notes.md"));
    }

    [Fact]
    public async Task WhenThePassageIsEmpty_ThenItIsRejected()
    {
        // Arrange
        WriteFile("Notes.md", "# Heading");
        var storage = await ConnectAsync();
        var markdown = Assert.IsType<MarkdownFile>(storage.Children["Notes.md"]);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => markdown.ReplaceTextAsync("", "x", CancellationToken.None));
        Assert.Equal("# Heading", ReadFile("Notes.md"));
    }

    [Fact]
    public async Task WhenTheReplacementMakesTheFileExceedTheSizeLimit_ThenItIsRejectedAndTheFileIsUnchanged()
    {
        // Arrange
        WriteFile("Notes.md", "# Heading");
        var storage = await ConnectAsync();
        var markdown = Assert.IsType<MarkdownFile>(storage.Children["Notes.md"]);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(
            () => markdown.ReplaceTextAsync("Heading", new string('a', MarkdownFile.MaxContentSize), CancellationToken.None));
        Assert.Equal("# Heading", ReadFile("Notes.md"));
    }

    [Fact]
    public async Task WhenTheFileChangedOnDiskSinceItWasLoaded_ThenThePassageIsReplacedInTheCurrentSource()
    {
        // Arrange
        WriteFile("Notes.md", "# Heading\n\nfirst");
        var storage = await ConnectAsync();
        var markdown = Assert.IsType<MarkdownFile>(storage.Children["Notes.md"]);
        WriteFile("Notes.md", "# Heading\n\nfirst\n\nadded by an editor");

        // Act
        await markdown.ReplaceTextAsync("first", "changed", CancellationToken.None);

        // Assert
        Assert.Equal("# Heading\n\nchanged\n\nadded by an editor", ReadFile("Notes.md"));
    }

    [Fact]
    public async Task WhenReplaceTextRunsDuringAnotherWrite_ThenItWaitsAndReplacesInTheWrittenSource()
    {
        // Arrange - an editor save whose content is still being written.
        WriteFile("Notes.md", "# Heading\n\nold");
        var storage = await ConnectAsync();
        var markdown = Assert.IsType<MarkdownFile>(storage.Children["Notes.md"]);
        using var savedContent = new GatedStream("# Heading\n\nsaved by the editor"u8.ToArray());
        var save = markdown.WriteAsync(savedContent, CancellationToken.None);
        await savedContent.Started;

        // Act
        var replace = markdown.ReplaceTextAsync("saved by the editor", "changed by an agent", CancellationToken.None);
        savedContent.Open();
        await Task.WhenAll(save, replace);

        // Assert
        Assert.Equal("# Heading\n\nchanged by an agent", ReadFile("Notes.md"));
        Assert.Equal("# Heading\n\nchanged by an agent", markdown.Content);
    }

    [Fact]
    public async Task WhenAConfigurationWriteBackRunsDuringAnotherWrite_ThenItWaitsAndWritesIntoTheWrittenSource()
    {
        // Arrange - an editor save whose content is still being written.
        WriteFile("Motors.md", "# Heading\n\n:: HomeBlaze.Samples.Motor#motor Name=Motor\n::\n");
        var storage = await ConnectAsync();
        var markdown = Assert.IsType<MarkdownFile>(storage.Children["Motors.md"]);
        var motor = Assert.IsType<Samples.Motor>(markdown.Children["motor"]);
        using var savedContent = new GatedStream("# Saved by the editor\n\n:: HomeBlaze.Samples.Motor#motor Name=Motor\n::\n"u8.ToArray());
        var save = markdown.WriteAsync(savedContent, CancellationToken.None);
        await savedContent.Started;

        // Act
        motor.TargetSpeed = 1500;
        var writeBack = markdown.WriteConfigurationAsync(motor, CancellationToken.None);
        savedContent.Open();
        await Task.WhenAll(save, writeBack);

        // Assert
        var content = ReadFile("Motors.md");
        Assert.StartsWith("# Saved by the editor\n\n:: HomeBlaze.Samples.Motor#motor ", content);
        Assert.Contains("TargetSpeed=1500", content);
        Assert.Equal(content, markdown.Content);
    }

    private const string PageWithActiveContent = """
        # Motors

        <div class="note">Handle with care</div>

        ![Wiring](https://example.com/wiring.png)

        :: HomeBlaze.Samples.Motor#motor Name=Motor TargetSpeed=1000
        ::

        The motor runs at {{ motor/CurrentSpeed }} rpm.

        :: HomeBlaze.Components.Card#card Title="Motor Card"
        Card body.

        ::: Footer
        Card footer
        :::
        ::

        :: HomeBlaze.Components.TabLayout#tabs
        ::: Tab Title="First Tab"
        First tab text
        :::
        ::

        :: HomeBlaze.Components.FlowLayout#flow MinWidth=240
        ::: Item
        :::: HomeBlaze.Components.Widget Path=motor
        ::::
        :::
        ::

        :: HomeBlaze.Samples.Motor#jsonmotor Name=JsonMotor
        ```json
        { "TargetSpeed": 500 }
        ```
        ::

        :: {.highlight}
        Styled text
        ::

        End of the page.
        """;

    [Theory]
    // Widget blocks: added with or without a closing line, an argument, the name or the json block changed, removed.
    [InlineData("End of the page.", "End of the page.\n\n:: HomeBlaze.Samples.Motor#other\n::")]
    [InlineData("End of the page.", "End of the page.\n\n:: HomeBlaze.Samples.Motor")]
    [InlineData("TargetSpeed=1000", "TargetSpeed=2000")]
    [InlineData("Motor#motor", "Motor#othermotor")]
    [InlineData(":: HomeBlaze.Samples.Motor#motor Name=Motor TargetSpeed=1000\n::\n\n", "")]
    [InlineData("\"TargetSpeed\": 500", "\"TargetSpeed\": 900")]
    [InlineData("Name=Motor TargetSpeed=1000\n", "Name=Motor TargetSpeed=1000\n```json\n{ \"SimulationInterval\": \"00:00:01\" }\n```\n")]
    // A layout or card: its content, a slot block added, changed or removed, a typed slot such as a tab.
    [InlineData("Card body.", "Changed body.")]
    [InlineData("Card footer", "Changed footer")]
    [InlineData("::: Footer\nCard footer\n:::\n", "")]
    [InlineData("Card body.\n", "Card body.\n\n::: HomeBlaze.Samples.Motor#cardmotor\n:::\n")]
    [InlineData("Title=\"First Tab\"", "Title=\"Renamed Tab\"")]
    [InlineData("First tab text\n:::\n", "First tab text\n:::\n::: Tab Title=\"Second Tab\"\nMore\n:::\n")]
    [InlineData("Path=motor", "Path=/Devices/Other")]
    [InlineData("MinWidth=240", "MinWidth=480")]
    [InlineData("::::\n:::\n::\n", "::::\n:::\n::: Item\nAnother item\n:::\n::\n")]
    // Style blocks, a closing line that closes nothing and a stray head line.
    [InlineData("Styled text", "Other styled text")]
    [InlineData("End of the page.", "End of the page.\n\n:: {.highlight}\nMore styled text\n::")]
    [InlineData("End of the page.", "End of the page.\n\n::")]
    [InlineData("The motor runs at", ":: Note\nThe motor runs at")]
    // Active markup.
    [InlineData("rpm.", "rpm. <img src=x onerror=alert(1)>")]
    [InlineData("rpm.", "rpm.\n\n<script>alert(1)</script>")]
    [InlineData("Handle with care", "Handle <b onclick=\"alert(1)\">with</b> care")]
    [InlineData("# Motors", "# Motors {onclick=\"alert(1)\"}")]
    [InlineData("rpm.", "rpm. [Details](javascript:alert(1))")]
    [InlineData("rpm.", "rpm. [Details](jav&#x61;script:alert(1))")]
    [InlineData("rpm.", "rpm. [Details][manual]\n\n[manual]: JavaScript:alert(1)")]
    [InlineData("rpm.", "rpm. <javascript:alert(1)>")]
    [InlineData("rpm.", "rpm.\n\n![](https://www.youtube.com/watch?v=x)")]
    [InlineData("rpm.", "rpm.\n\n![](https://h/x.mp4)")]
    [InlineData("rpm.", "rpm. ![Pixel](https://tracker.example.com/pixel.png)")]
    [InlineData("rpm.", "rpm. ![Pixel][pixel]\n\n[pixel]: https://tracker.example.com/pixel.png")]
    [InlineData("rpm.", "rpm.\n\n:::warning\nCareful\n:::")]
    [InlineData("rpm.", "rpm.\n\n*[motor]: The motor of the page")]
    [InlineData("rpm.", "rpm.\n\n$$\nx^2\n$$")]
    [InlineData("rpm.", "rpm.\n\n```mermaid\ngraph TD; A-->B\n```")]
    [InlineData("(https://example.com/wiring.png)", "(https://example.com/other.png)")]
    public async Task WhenReplacingTextAddsChangesOrRemovesActiveContent_ThenItIsRejectedAndTheFileIsUnchanged(string oldText, string newText)
    {
        // Arrange
        WriteFile("Motors.md", PageWithActiveContent);
        var storage = await ConnectAsync();
        var markdown = Assert.IsType<MarkdownFile>(storage.Children["Motors.md"]);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => markdown.ReplaceTextAsync(oldText, newText, CancellationToken.None));
        Assert.StartsWith("Blocks cannot be added, changed or removed, and HTML, images and media", exception.Message);
        Assert.Equal(PageWithActiveContent, ReadFile("Motors.md"));
    }

    [Theory]
    [InlineData("The motor runs at", "The conveyor motor runs at")]
    [InlineData("End of the page.", "End of the page, with a note.\n\nAnother paragraph.")]
    [InlineData("# Motors", "# All motors")]
    [InlineData("rpm.", "rpm. See [the manual](https://example.com/manual) or [the notes](Notes.md).")]
    [InlineData("rpm.", "rpm.\n\n| Speed | State |\n|:-----:|------:|\n| 1000 | ok |\n\n- [x] checked[^1]\n\n[^1]: A footnote.")]
    [InlineData("<div class=\"note\">Handle with care</div>\n\n", "")]
    [InlineData("![Wiring](https://example.com/wiring.png)\n\n", "")]
    [InlineData("rpm.", "rpm.\n\n```csharp\nvar speed = 1000;\n```")]
    [InlineData("rpm.", "rpm.\n\n```text\n:: HomeBlaze.Samples.Motor\n::\n```")]
    [InlineData("# Motors", "# שלום עולם {dir=rtl}")]
    public async Task WhenReplacingTextKeepsTheBlocksAndTheActiveContent_ThenTheTextIsChanged(string oldText, string newText)
    {
        // Arrange
        WriteFile("Motors.md", PageWithActiveContent);
        var storage = await ConnectAsync();
        var markdown = Assert.IsType<MarkdownFile>(storage.Children["Motors.md"]);

        // Act
        await markdown.ReplaceTextAsync(oldText, newText, CancellationToken.None);

        // Assert
        Assert.Equal(PageWithActiveContent.Replace(oldText, newText), ReadFile("Motors.md"));
    }

    [Fact]
    public async Task WhenTextIsAddedAfterABlockWithoutAClosingLine_ThenItIsRejectedBecauseItBelongsToTheBlock()
    {
        // Arrange
        const string content = "# Motors\n\n:: HomeBlaze.Components.Card Title=Notes\nCard text";
        WriteFile("Motors.md", content);
        var storage = await ConnectAsync();
        var markdown = Assert.IsType<MarkdownFile>(storage.Children["Motors.md"]);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(
            () => markdown.ReplaceTextAsync("Card text", "Card text\n\nAppended by an agent.", CancellationToken.None));
        Assert.Equal(content, ReadFile("Motors.md"));
    }

    private const string PageWithUnnamedBlock = "# Motors\n\n:: HomeBlaze.Samples.Motor Name=Unnamed\n::\n\nAfter the motor.";

    [Theory]
    [InlineData("# Motors", "# Motors at {{ /Devices/Speed }}")]
    [InlineData("# Motors\n\n", "# Motors\n\nThe speed is {{ /Devices/Speed }}.\n\n")]
    public async Task WhenAnExpressionIsAddedBeforeABlockWithoutAName_ThenItIsRejectedBecauseTheSubjectWouldStartAgain(string oldText, string newText)
    {
        // Arrange
        WriteFile("Motors.md", PageWithUnnamedBlock);
        var storage = await ConnectAsync();
        var markdown = Assert.IsType<MarkdownFile>(storage.Children["Motors.md"]);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => markdown.ReplaceTextAsync(oldText, newText, CancellationToken.None));
        Assert.StartsWith("The change adds or removes an expression, the first text, or text between two blocks before a block without a #name", exception.Message);
        Assert.Equal(PageWithUnnamedBlock, ReadFile("Motors.md"));
    }

    [Theory]
    [InlineData("#main", true)]
    [InlineData("", false)]
    public async Task WhenAnExpressionIsAddedBeforeACardWithAnUnnamedWidget_ThenOnlyAnUnnamedCardRefusesIt(string cardName, bool isAllowed)
    {
        // Arrange: the widget inside the card has no name, and its key counts from the content of the card.
        var content = $"# Cards\n\n:: HomeBlaze.Components.Card{cardName} Title=Main\n::: HomeBlaze.Components.Widget Path=/Devices\n:::\n::\n";
        WriteFile("Cards.md", content);
        var storage = await ConnectAsync();
        var markdown = Assert.IsType<MarkdownFile>(storage.Children["Cards.md"]);

        // Act
        var exception = await Record.ExceptionAsync(
            () => markdown.ReplaceTextAsync("# Cards", "# Cards at {{ /Devices/Speed }}", CancellationToken.None));

        // Assert
        if (isAllowed)
        {
            Assert.Null(exception);
            Assert.Equal(content.Replace("# Cards", "# Cards at {{ /Devices/Speed }}"), ReadFile("Cards.md"));
        }
        else
        {
            Assert.StartsWith("The change adds or removes an expression", Assert.IsType<ArgumentException>(exception).Message);
            Assert.Equal(content, ReadFile("Cards.md"));
        }
    }

    [Fact]
    public async Task WhenTheFirstTextIsAddedBeforeABlockWithoutAName_ThenItIsRejectedBecauseTheSubjectWouldStartAgain()
    {
        // Arrange
        const string content = ":: HomeBlaze.Samples.Motor Name=Unnamed\n::\n\nAfter the motor.";
        WriteFile("Motors.md", content);
        var storage = await ConnectAsync();
        var markdown = Assert.IsType<MarkdownFile>(storage.Children["Motors.md"]);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => markdown.ReplaceContentAsync("# Motors\n\n" + content, CancellationToken.None));
        Assert.StartsWith("The change adds or removes an expression, the first text", exception.Message);
        Assert.Equal(content, ReadFile("Motors.md"));
    }

    [Theory]
    [InlineData("# Motors", "# All motors\n\nA paragraph added before the motor.")]
    [InlineData("After the motor.", "After the motor at {{ /Devices/Speed }}.\n\nAnd more.")]
    public async Task WhenTextChangesBeforeOrAfterABlockWithoutANameWithoutMovingIt_ThenTheTextIsChanged(string oldText, string newText)
    {
        // Arrange
        WriteFile("Motors.md", PageWithUnnamedBlock);
        var storage = await ConnectAsync();
        var markdown = Assert.IsType<MarkdownFile>(storage.Children["Motors.md"]);

        // Act
        await markdown.ReplaceTextAsync(oldText, newText, CancellationToken.None);

        // Assert
        Assert.Equal(PageWithUnnamedBlock.Replace(oldText, newText), ReadFile("Motors.md"));
    }

    [Theory]
    [InlineData("- Item\n\n  :: HomeBlaze.Samples.Motor Password=s3cr3t\n  ```json\n  {\n    \"Password\": \"json-secret\"\n  }\n  ```\n  ::\n\nText at the end.")]
    [InlineData("> :: HomeBlaze.Samples.Motor Password=s3cr3t\n> ```json\n> {\n>   \"Password\": \"json-secret\"\n> }\n> ```\n> ::\n\nText at the end.")]
    [InlineData(":: Password=s3cr3t\n::\n\nText at the end.")]
    [InlineData("# Old\n\n```subject(gateway)\n{ \"password\": \"s3cr3t\" }\n```\n\nText at the end.")]
    public async Task WhenAPageHasHiddenValuesOutsideOfValidBlocks_ThenAnUnrelatedEditKeepsThemAndWritesNothingElse(string content)
    {
        // Arrange
        WriteFile("Motors.md", content);
        var storage = await ConnectAsync();
        var markdown = Assert.IsType<MarkdownFile>(storage.Children["Motors.md"]);

        // Act
        await markdown.ReplaceTextAsync("Text at the end.", "Changed text at the end.", CancellationToken.None);

        // Assert
        Assert.Equal(content.Replace("Text at the end.", "Changed text at the end."), ReadFile("Motors.md"));
        Assert.DoesNotContain("s3cr3t", markdown.ReadRawContent());
    }

    [Theory]
    [InlineData("# Old\n\n```subject(gateway)\n{ \"password\": \"s3cr3t\" }\n```\n\nText.")]
    [InlineData("# Old\n\nText.\n\n```subject(gateway)\n{ \"password\": \"s3cr3t\" }\n")]
    public async Task WhenAnEditChangesAnOldSubjectFence_ThenItIsRejectedAndTheFileIsUnchanged(string content)
    {
        // Arrange
        WriteFile("Motors.md", content);
        var storage = await ConnectAsync();
        var markdown = Assert.IsType<MarkdownFile>(storage.Children["Motors.md"]);
        var view = markdown.ReadRawContent()!;

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => markdown.ReplaceContentAsync(view.Replace("***", "{ }"), CancellationToken.None));
        Assert.StartsWith("Blocks cannot be added, changed or removed", exception.Message);
        Assert.Equal(content, ReadFile("Motors.md"));
    }

    [Fact]
    public async Task WhenTextIsAddedAfterAnOldSubjectFenceWithoutAClosingFence_ThenItIsRejectedBecauseItBelongsToTheFence()
    {
        // Arrange
        const string content = "# Old\n\n```subject(gateway)\n{ \"password\": \"s3cr3t\" }\n";
        WriteFile("Motors.md", content);
        var storage = await ConnectAsync();
        var markdown = Assert.IsType<MarkdownFile>(storage.Children["Motors.md"]);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => markdown.ReplaceContentAsync(markdown.ReadRawContent() + "\nMore text.", CancellationToken.None));
        Assert.StartsWith("Blocks cannot be added, changed or removed", exception.Message);
        Assert.Equal(content, ReadFile("Motors.md"));
    }

    [Fact]
    public async Task WhenThePageHasWindowsLineEndingsAndTheNewContentHasLineFeeds_ThenTheBlocksKeepTheirText()
    {
        // Arrange
        const string content = "# Motors\r\n\r\n:: HomeBlaze.Samples.Motor#motor Name=Motor\r\n::\r\n\r\nText";
        WriteFile("Motors.md", content);
        var storage = await ConnectAsync();
        var markdown = Assert.IsType<MarkdownFile>(storage.Children["Motors.md"]);

        // Act
        await markdown.ReplaceContentAsync("# All motors\n\n:: HomeBlaze.Samples.Motor#motor Name=Motor\n::\n\nText", CancellationToken.None);

        // Assert
        Assert.Equal("# All motors\n\n:: HomeBlaze.Samples.Motor#motor Name=Motor\r\n::\n\nText", ReadFile("Motors.md"));
    }

    [Fact]
    public async Task WhenReplacingTheContentRemovesABlock_ThenItIsRejected()
    {
        // Arrange
        WriteFile("Motors.md", PageWithActiveContent);
        var storage = await ConnectAsync();
        var markdown = Assert.IsType<MarkdownFile>(storage.Children["Motors.md"]);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(
            () => markdown.ReplaceContentAsync("# Motors\n\nNo motor any more.", CancellationToken.None));
        Assert.Equal(PageWithActiveContent, ReadFile("Motors.md"));
    }

    [Fact]
    public async Task WhenReplacingTheContentSwapsTwoBlocks_ThenItIsRejected()
    {
        // Arrange
        const string content = "# Motors\n\n:: HomeBlaze.Samples.Motor#first\n::\n\n:: HomeBlaze.Samples.Motor#second\n::\n";
        WriteFile("Motors.md", content);
        var storage = await ConnectAsync();
        var markdown = Assert.IsType<MarkdownFile>(storage.Children["Motors.md"]);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => markdown.ReplaceContentAsync(
            "# Motors\n\n:: HomeBlaze.Samples.Motor#second\n::\n\n:: HomeBlaze.Samples.Motor#first\n::\n", CancellationToken.None));
        Assert.Equal(content, ReadFile("Motors.md"));
    }

    [Fact]
    public async Task WhenReplacingTheContentKeepsTheBlocksAndHtml_ThenItIsWritten()
    {
        // Arrange
        WriteFile("Motors.md", PageWithActiveContent);
        var storage = await ConnectAsync();
        var markdown = Assert.IsType<MarkdownFile>(storage.Children["Motors.md"]);
        var content = PageWithActiveContent.Replace("# Motors", "# All motors") + "\n\nUpdated by an agent.";

        // Act
        await markdown.ReplaceContentAsync(content, CancellationToken.None);

        // Assert
        Assert.Equal(content, ReadFile("Motors.md"));
    }

    [Theory]
    [InlineData("# Notes\n\n:: HomeBlaze.Samples.Motor#motor Name=Motor\n::")]
    [InlineData("# Notes\n\n:: HomeBlaze.Samples.Motor")]
    [InlineData("# Notes\n\n:: HomeBlaze.Samples.Motor\n```json\n{ \"TargetSpeed\": 500 }\n```\n::")]
    [InlineData("# Notes\n\n:: HomeBlaze.Components.Card Title=Notes\nText\n\n::: Footer\nFooter\n:::\n::")]
    [InlineData("# Notes\n\n:: HomeBlaze.Components.TabLayout\n::: Tab Title=First\nText\n:::\n::")]
    [InlineData("# Notes\n\n:: HomeBlaze.Components.FlowLayout\n::: Item\nText\n:::\n::")]
    [InlineData("# Notes\n\n:: HomeBlaze.Components.Widget Path=/Devices/Motor\n::")]
    [InlineData("# Notes\n\n:: {.highlight}\nText\n::")]
    [InlineData("# Notes\n\n::")]
    [InlineData(":::note\nText\n:::")]
    [InlineData("# Notes\n\n<img src=x onerror=alert(1)>")]
    [InlineData("# Notes <span>inline</span>")]
    [InlineData("# Notes {style=\"color:red\"}")]
    [InlineData("[Run](javascript:alert(1))")]
    [InlineData("![](https://www.youtube.com/watch?v=x)")]
    public async Task WhenANewFileHasBlocksOrActiveContent_ThenCreatingItIsRejected(string content)
    {
        // Arrange
        var storage = await ConnectAsync();

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => storage.CreateMarkdownFileAsync("Notes.md", content, CancellationToken.None));
        Assert.StartsWith("Blocks cannot be added, changed or removed", exception.Message);
        Assert.False(File.Exists(GetFullPath("Notes.md")));
    }

    [Fact]
    public async Task WhenTheStorageIsNotLocal_ThenTheWriteOperationsAreRefusedWithoutWriting()
    {
        // Arrange
        var storage = new Mock<IStorageContainer>();
        var (parser, serializer) = CreateParserAndSerializer();
        var markdown = new MarkdownFile(storage.Object, "Notes.md", parser, serializer);

        // Act & Assert
        var replaceContent = await Assert.ThrowsAsync<InvalidOperationException>(
            () => markdown.ReplaceContentAsync("# New", CancellationToken.None));
        var replaceText = await Assert.ThrowsAsync<InvalidOperationException>(
            () => markdown.ReplaceTextAsync("# Old", "# New", CancellationToken.None));
        Assert.Contains("local", replaceContent.Message);
        Assert.Contains("local", replaceText.Message);
        Assert.False(markdown.IsLocal);
        storage.Verify(container => container.WriteBlobAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Never);
        storage.Verify(container => container.ReadBlobAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task WhenTheStorageTypeIsNotLocal_ThenCreatingAMarkdownFileIsRefused()
    {
        // Arrange
        var storage = await ConnectAsync();
        var folder = new VirtualFolder(storage, "Docs/");
        storage.StorageType = "azure-blob";

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => storage.CreateMarkdownFileAsync("Notes.md", "# Notes", CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => folder.CreateMarkdownFileAsync("Notes.md", "# Notes", CancellationToken.None));
        Assert.False(storage.IsLocal);
        Assert.False(folder.IsLocal);
        Assert.False(File.Exists(GetFullPath("Notes.md")));
        Assert.False(File.Exists(GetFullPath("Docs/Notes.md")));
    }

    [Fact]
    public async Task WhenAFolderBelongsToAnotherStorage_ThenCreatingAMarkdownFileIsRefused()
    {
        // Arrange
        var folder = new VirtualFolder(Mock.Of<IStorageContainer>(), "Docs/");

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => folder.CreateMarkdownFileAsync("Notes.md", "# Notes", CancellationToken.None));
        Assert.False(folder.IsLocal);
    }

    [Theory]
    [InlineData("disk", true)]
    [InlineData("filesystem", true)]
    [InlineData("inmemory", true)]
    [InlineData("azure-blob", false)]
    public void WhenCheckingTheStorageType_ThenOnlyDiskAndInMemoryStorageIsLocal(string storageType, bool expected)
    {
        // Arrange
        var storage = CreateStorage();

        // Act
        storage.StorageType = storageType;

        // Assert
        Assert.Equal(expected, storage.IsLocal);
        Assert.Equal(expected, new VirtualFolder(storage, "Docs/").IsLocal);
    }

    [Fact]
    public async Task WhenCreatingAMarkdownFileInTheRoot_ThenItIsWrittenAndAddedAsAPage()
    {
        // Arrange
        var storage = await ConnectAsync();

        // Act
        await storage.CreateMarkdownFileAsync("Notes.md", "# Notes", CancellationToken.None);

        // Assert
        Assert.Equal("# Notes", ReadFile("Notes.md"));
        var markdown = Assert.IsType<MarkdownFile>(storage.Children["Notes.md"]);
        Assert.Equal("# Notes", markdown.Content);
        Assert.Equal("/Notes.md", markdown.FullPath);
        Assert.Contains(markdown.Children.Values.OfType<HtmlSegment>(), segment => segment.Html.Contains("Notes"));
    }

    [Fact]
    public async Task WhenCreatingAMarkdownFileInAFolder_ThenItIsAddedToTheFolderAndCanBeEdited()
    {
        // Arrange
        WriteFile("Docs/Existing.md", "# Existing");
        var storage = await ConnectAsync();
        var folder = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);

        // Act
        await folder.CreateMarkdownFileAsync("Notes.MD", "# Notes", CancellationToken.None);
        var markdown = Assert.IsType<MarkdownFile>(Assert.IsType<VirtualFolder>(storage.Children["Docs"]).Children["Notes.MD"]);
        await markdown.ReplaceTextAsync("# Notes", "# Changed", CancellationToken.None);

        // Assert
        Assert.Equal("/Docs/Notes.MD", markdown.FullPath);
        Assert.Equal("# Changed", ReadFile("Docs/Notes.MD"));
        Assert.Equal("# Changed", markdown.Content);
        Assert.True(Assert.IsType<VirtualFolder>(storage.Children["Docs"]).Children.ContainsKey("Existing.md"));
    }

    [Fact]
    public async Task WhenTheFileAlreadyExists_ThenCreatingItIsRejectedAndTheFileIsUnchanged()
    {
        // Arrange
        WriteFile("Docs/Notes.md", "# Original");
        var storage = await ConnectAsync();
        var folder = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => folder.CreateMarkdownFileAsync("Notes.md", "# Replaced", CancellationToken.None));
        Assert.Contains("already exists", exception.Message);
        Assert.Equal("# Original", ReadFile("Docs/Notes.md"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("notes")]
    [InlineData("notes.txt")]
    [InlineData("notes.json")]
    [InlineData("notes.markdown")]
    [InlineData(".md")]
    [InlineData(".hidden.md")]
    [InlineData("~notes.md")]
    [InlineData("notes.tmp.md")]
    [InlineData(" notes.md")]
    [InlineData("notes.md ")]
    [InlineData("notes.md.")]
    [InlineData("Docs/notes.md")]
    [InlineData("../notes.md")]
    [InlineData("..\\notes.md")]
    [InlineData("no:tes.md")]
    [InlineData("no*tes.md")]
    [InlineData("no\ntes.md")]
    [InlineData("no\u200Btes.md")]
    [InlineData("notes\u202Edm.txt.md")]
    public async Task WhenTheNameIsNotAPlainMarkdownFileName_ThenCreatingTheFileIsRejected(string name)
    {
        // Arrange
        var storage = await ConnectAsync();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(
            () => storage.CreateMarkdownFileAsync(name, "# Notes", CancellationToken.None));
        Assert.Empty(storage.Children);
        Assert.Empty(_directory.EnumerateFileSystemInfos());
    }

    [Fact]
    public async Task WhenTheContentOfANewFileExceedsTheSizeLimit_ThenCreatingItIsRejected()
    {
        // Arrange
        var storage = await ConnectAsync();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(
            () => storage.CreateMarkdownFileAsync("Notes.md", new string('a', MarkdownFile.MaxContentSize + 1), CancellationToken.None));
        Assert.False(File.Exists(GetFullPath("Notes.md")));
    }

    [Fact]
    public async Task WhenTheStorageIsInMemory_ThenAMarkdownFileCanBeCreatedAndEdited()
    {
        // Arrange
        var storage = await ConnectAsync(storageType: "inmemory");

        // Act
        await storage.CreateMarkdownFileAsync("Notes.md", "# Notes", CancellationToken.None);
        var markdown = Assert.IsType<MarkdownFile>(storage.Children["Notes.md"]);
        await markdown.ReplaceContentAsync("# Replaced", CancellationToken.None);

        // Assert
        Assert.Equal("# Replaced", markdown.Content);
    }

    [Fact]
    public async Task WhenPagesAndFoldersAreInTheGraph_ThenTheWriteOperationsAreRegisteredWithoutConfirmation()
    {
        // Arrange
        WriteFile("Docs/Notes.md", "# Notes");

        // Act
        var storage = await ConnectAsync();
        var folder = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);
        var markdown = Assert.IsType<MarkdownFile>(folder.Children["Notes.md"]);

        // Assert
        AssertOperation(markdown, "ReplaceContent", "Replace Content", "content");
        AssertOperation(markdown, "ReplaceText", "Replace Text", "oldText", "newText");
        AssertOperation(folder, "CreateMarkdownFile", "Create Markdown File", "name", "content");
        AssertOperation(storage, "CreateMarkdownFile", "Create Markdown File", "name", "content");
    }

    private static void AssertOperation(IInterceptorSubject subject, string name, string title, params string[] parameterNames)
    {
        var method = Assert.IsType<MethodMetadata>(subject.TryGetRegisteredSubject()!.TryGetProperty(name)!.GetValue());
        Assert.Equal(MethodKind.Operation, method.Kind);
        Assert.Equal(title, method.Title);
        Assert.False(method.RequiresConfirmation);
        Assert.False(string.IsNullOrEmpty(method.Description));
        Assert.Equal(parameterNames, method.Parameters.Where(parameter => parameter.RequiresInput).Select(parameter => parameter.Name));
        Assert.All(method.Parameters.Where(parameter => parameter.RequiresInput), parameter => Assert.Equal(typeof(string), parameter.Type));
    }

    private FluentStorageContainer CreateStorage(string storageType = "disk")
    {
        var services = CreateServices(out var context);
        var serviceProvider = services.BuildServiceProvider();
        var storage = new FluentStorageContainer(
            serviceProvider.GetRequiredService<SubjectTypeRegistry>(),
            serviceProvider.GetRequiredService<ConfigurableSubjectSerializer>(),
            serviceProvider)
        {
            StorageType = storageType,
            ConnectionString = _directory.FullName,
            EnableFileWatching = false
        };

        ((IInterceptorSubject)storage).Context.AddFallbackContext(context);
        _storages.Add(storage);
        return storage;
    }

    private async Task<FluentStorageContainer> ConnectAsync(string storageType = "disk")
    {
        var storage = CreateStorage(storageType);
        await storage.ConnectAsync(CancellationToken.None);
        return storage;
    }

    private static ServiceCollection CreateServices(out IInterceptorSubjectContext context)
    {
        var typeProvider = new TypeProvider();
        typeProvider.AddAssembly(typeof(FluentStorageContainer).Assembly);
        typeProvider.AddAssembly(typeof(Samples.Motor).Assembly);
        typeProvider.AddAssembly(typeof(Components.Card).Assembly);

        var services = new ServiceCollection();
        context = SubjectContextFactory.Create(services);
        services.AddSingleton(typeProvider);
        services.AddSingleton(new SubjectTypeRegistry(typeProvider));
        services.AddSingleton(context);
        services.AddSingleton<SubjectFactory>();
        services.AddSingleton<ConfigurableSubjectSerializer>();
        services.AddSingleton<RootManager>();
        services.AddSingleton(sp => new SubjectPathResolver(() => sp.GetRequiredService<RootManager>().Root));
        services.AddSingleton<MarkdownContentParser>();
        return services;
    }

    private static (MarkdownContentParser Parser, ConfigurableSubjectSerializer Serializer) CreateParserAndSerializer()
    {
        var serviceProvider = CreateServices(out _).BuildServiceProvider();
        return (serviceProvider.GetRequiredService<MarkdownContentParser>(), serviceProvider.GetRequiredService<ConfigurableSubjectSerializer>());
    }

    private string GetFullPath(string relativePath)
        => Path.Combine(_directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private void WriteFile(string relativePath, string content)
    {
        var fullPath = GetFullPath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private string ReadFile(string relativePath) => File.ReadAllText(GetFullPath(relativePath));

    public void Dispose()
    {
        foreach (var storage in _storages)
        {
            storage.Dispose();
        }

        _directory.Delete(recursive: true);
    }

    /// <summary>A stream whose reads wait until <see cref="Open"/> is called.</summary>
    private sealed class GatedStream(byte[] content) : MemoryStream(content, writable: false)
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public void Open() => _gate.TrySetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _started.TrySetResult();
            await _gate.Task.WaitAsync(cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count)
        {
            _started.TrySetResult();
            _gate.Task.Wait();
            return base.Read(buffer, offset, count);
        }

        public override int Read(Span<byte> buffer)
        {
            _started.TrySetResult();
            _gate.Task.Wait();
            return base.Read(buffer);
        }
    }
}
