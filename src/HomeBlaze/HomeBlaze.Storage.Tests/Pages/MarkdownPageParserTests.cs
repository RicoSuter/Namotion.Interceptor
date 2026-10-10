using HomeBlaze.Storage.Pages;

namespace HomeBlaze.Storage.Tests.Pages;

public class MarkdownPageParserTests
{
    private static string Normalize(string text) => text.Replace("\r\n", "\n");

    [Fact]
    public void WhenPageHasNestedBlocks_ThenTreeFollowsTheFences()
    {
        // Arrange
        var source = Normalize("""
            ---
            title: Test
            ---

            text

            :::::Demo.TabLayout#tabs
            ::::Tab title="Heizung {x}"
            Body

            :::Demo.Widget path="/House/{Heizung}"
            :::
            ::::
            ::::Tab title="Strom"
            ::::
            :::::
            """);

        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        var tabs = Assert.Single(page.Blocks);
        Assert.Equal(MarkdownBlockKind.Widget, tabs.Kind);
        Assert.Equal(7, tabs.Line);
        Assert.Equal("tabs", tabs.Head!.Id);
        Assert.Equal(2, tabs.Children.Count);
        Assert.All(tabs.Children, tab => Assert.Equal(MarkdownBlockKind.Slot, tab.Kind));
        var widget = Assert.Single(tabs.Children[0].Children);
        Assert.Equal("/House/{Heizung}", widget.Head!.Arguments[0].Value);
        Assert.Equal([0, 1, 2, 3], page.AllBlocks.Select(block => block.Index));
    }

    [Fact]
    public void WhenBlockHasHeadLine_ThenHeadSpanCoversExactlyThatLine()
    {
        // Arrange
        var source = Normalize("""
            before

            :::Demo.Motor#m speed=3000
            :::
            """);

        // Act
        var block = MarkdownPageParser.Parse(source).Blocks[0];

        // Assert
        Assert.Equal(":::Demo.Motor#m speed=3000", source.Substring(block.HeadSpan.Start, block.HeadSpan.Length));
    }

    [Fact]
    public void WhenFirstChildIsJsonFence_ThenItIsTheConfiguration()
    {
        // Arrange
        var source = Normalize("""
            :::Demo.Grid rows=1
            ```json
            { "cells": [1, 2] }
            ```
            :::
            """);

        // Act
        var block = MarkdownPageParser.Parse(source).Blocks[0];

        // Assert
        Assert.Equal("{ \"cells\": [1, 2] }", block.ConfigurationJson);
        var span = block.ConfigurationSpan!.Value;
        Assert.StartsWith("```json", source.Substring(span.Start, span.Length));
        Assert.EndsWith("```", source.Substring(span.Start, span.Length));
    }

    [Fact]
    public void WhenBlockIsInsideListItem_ThenItIsInvalid()
    {
        // Arrange
        var source = Normalize("""
            - item
              :::Demo.Card
              text
              :::
            """);

        // Act
        var block = Assert.Single(MarkdownPageParser.Parse(source).AllBlocks);

        // Assert
        Assert.Equal(MarkdownBlockKind.Invalid, block.Kind);
        Assert.Contains("only allowed directly on the page", block.Error);
    }

    [Fact]
    public void WhenUnnamedBlockHasAttributes_ThenItIsAStyleBlock()
    {
        // Arrange
        var source = Normalize("""
            ::: {.band}
            text
            :::
            """);

        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        Assert.Equal(MarkdownBlockKind.Style, Assert.Single(page.Blocks).Kind);
        Assert.Empty(page.AllBlocks);
    }

    [Fact]
    public void WhenWidgetIsInsideStyleBlock_ThenItIsInvalid()
    {
        // Arrange
        var source = Normalize("""
            :::: {.band}
            :::Demo.Card
            :::
            ::::
            """);

        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        Assert.Equal(MarkdownBlockKind.Invalid, Assert.Single(page.AllBlocks).Kind);
    }

    [Fact]
    public void WhenInnerFenceIsAsLongAsOuter_ThenAnUnnamedBlockIsReported()
    {
        // Arrange
        var source = Normalize("""
            :::Demo.Card
            :::Demo.Widget path=/x
            :::
            after
            :::
            rest
            """);

        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        var unnamed = page.AllBlocks.Single(block => block.Kind == MarkdownBlockKind.Invalid);
        Assert.Contains("outer block must be longer", unnamed.Error);
    }

    [Fact]
    public void WhenSlotBlockIsAtPageLevel_ThenItIsInvalid()
    {
        // Arrange
        var source = Normalize("""
            :::Footer
            text
            :::
            """);

        // Act
        var block = Assert.Single(MarkdownPageParser.Parse(source).AllBlocks);

        // Assert
        Assert.Equal(MarkdownBlockKind.Invalid, block.Kind);
        Assert.Contains("not a type name", block.Error);
    }

    [Fact]
    public void WhenHeadLineIsMalformed_ThenBlockCarriesTheGrammarError()
    {
        // Arrange
        var source = Normalize("""
            :::Demo.Card title="open
            :::
            """);

        // Act
        var block = Assert.Single(MarkdownPageParser.Parse(source).AllBlocks);

        // Assert
        Assert.Equal(MarkdownBlockKind.Invalid, block.Kind);
        Assert.Contains("closing quote", block.Error);
    }
}
