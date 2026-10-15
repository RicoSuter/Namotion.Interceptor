using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Storage.Internal;
using HomeBlaze.Storage.Pages;

namespace HomeBlaze.Storage.Tests;

public class MarkdownPageLayoutTests
{
    [Fact]
    public void WhenPropertiesAreMarked_ThenTheLayoutTypeListsEachKind()
    {
        // Act
        var layoutType = MarkdownPageLayout.GetLayoutType(typeof(TwoReferenceLayout));

        // Assert
        Assert.NotNull(layoutType);
        Assert.Equal(["Primary", "Secondary"], layoutType.ReferenceProperties.Order());
        Assert.Equal(["Link"], layoutType.LinkProperties);
        Assert.Equal(["Picture"], layoutType.MediaProperties);
    }

    [Fact]
    public void WhenATypeHasTwoReferenceProperties_ThenBothPathsAreReturned()
    {
        // Arrange
        var page = MarkdownPageParser.Parse($":: {LayoutTypeName} Primary=first Secondary=second\n::");
        var block = Assert.Single(MarkdownPageLayout.FindLayoutBlocks(page, FindType));

        // Act
        var paths = MarkdownPageLayout.GetReferencePaths(block, typeof(TwoReferenceLayout), FindType);

        // Assert
        Assert.Equal(["first", "second"], paths.Order());
    }

    [Fact]
    public void WhenATypeHasNoLayoutAttribute_ThenItIsNoLayoutType()
    {
        // Act
        var layoutType = MarkdownPageLayout.GetLayoutType(typeof(PlainType));

        // Assert
        Assert.Null(layoutType);
    }

    // A nested type's full name contains '+', which a block head does not accept.
    private const string LayoutTypeName = "Tests.TwoReferenceLayout";

    private static Type? FindType(string fullName) => fullName == LayoutTypeName ? typeof(TwoReferenceLayout) : null;

    [PageLayout]
    private sealed class TwoReferenceLayout
    {
        [Configuration]
        [PageLayoutReference]
        public string? Primary { get; set; }

        [Configuration]
        [PageLayoutReference]
        public string? Secondary { get; set; }

        [Configuration]
        [PageLayoutLink]
        public string? Link { get; set; }

        [Configuration]
        [PageLayoutMedia]
        public string? Picture { get; set; }
    }

    private sealed class PlainType
    {
        [Configuration]
        [PageLayoutReference]
        public string? Path { get; set; }
    }
}
