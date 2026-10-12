using HomeBlaze.Components;
using HomeBlaze.Storage.Files;
using MudBlazor;
using Xunit;

namespace HomeBlaze.Storage.Tests.Pages;

[Collection(MarkdownPageFixture.StorageCollection)]
public class ValueWidgetBlockTests
{
    private static async Task<MarkdownFile> AddPageAsync(string content)
    {
        var fixture = await MarkdownPageFixture.CreateWithWidgetsAndSamplesAsync();
        return await fixture.AddPageAsync("page.md", content);
    }

    [Fact]
    public async Task WhenValueListHasRows_ThenEachRowHasItsOptionsAndContent()
    {
        // Arrange & Act
        var page = await AddPageAsync("""
            :: HomeBlaze.Components.ValueList#list
            ::: Value Path=/a/Speed Label="Speed" Icon=Speed Map=states
            ::: Value Label="Two"
            Row text
            ::
            """);

        // Assert
        Assert.DoesNotContain("markdown-block-error", MarkdownPageFixture.Html(page.Children));
        var list = Assert.IsType<ValueList>(page.Children["list"]);
        Assert.Equal(2, list.Values.Length);
        Assert.Equal("/a/Speed", list.Values[0].Path);
        Assert.Equal("Speed", list.Values[0].Label);
        Assert.Equal("Speed", list.Values[0].Icon);
        Assert.Equal("states", list.Values[0].Map);
        Assert.Null(list.Values[0].Content);
        Assert.Null(list.Values[1].Path);
        Assert.NotNull(list.Values[1].Content);
    }

    [Fact]
    public async Task WhenPageHasValueTile_ThenItHasItsOptions()
    {
        // Arrange & Act
        var page = await AddPageAsync("""
            :: HomeBlaze.Components.ValueTile#tile Path=/a/Speed Label="Speed" Icon=Speed Map=states
            ::
            """);

        // Assert
        var tile = Assert.IsType<ValueTile>(page.Children["tile"]);
        Assert.Equal("/a/Speed", tile.Path);
        Assert.Equal("Speed", tile.Label);
        Assert.Equal("Speed", tile.Icon);
        Assert.Equal("states", tile.Map);
    }

    [Fact]
    public async Task WhenPageHasIndicatorAndProgress_ThenTheyHaveTheirOptionsAndDefaults()
    {
        // Arrange & Act
        var page = await AddPageAsync("""
            :: HomeBlaze.Components.Indicator#plain Path=/a/IsOn
            ::

            :: HomeBlaze.Components.Indicator#icon Path=/a/IsOn Map=states Icon=Lightbulb Kind=Icon
            ::

            :: HomeBlaze.Components.Progress#plainbar Path=/a/Position
            ::

            :: HomeBlaze.Components.Progress#bar Path=/a/Speed Minimum=10 Maximum=1600 Color=Success
            ::
            """);

        // Assert
        Assert.DoesNotContain("markdown-block-error", MarkdownPageFixture.Html(page.Children));

        var plain = Assert.IsType<Indicator>(page.Children["plain"]);
        Assert.Equal(IndicatorKind.Chip, plain.Kind);

        var icon = Assert.IsType<Indicator>(page.Children["icon"]);
        Assert.Equal(IndicatorKind.Icon, icon.Kind);
        Assert.Equal("Lightbulb", icon.Icon);
        Assert.Equal("states", icon.Map);

        var plainBar = Assert.IsType<Progress>(page.Children["plainbar"]);
        Assert.Equal(0, plainBar.Minimum);
        Assert.Equal(1, plainBar.Maximum);
        Assert.Equal(Color.Primary, plainBar.Color);

        var bar = Assert.IsType<Progress>(page.Children["bar"]);
        Assert.Equal(10, bar.Minimum);
        Assert.Equal(1600, bar.Maximum);
        Assert.Equal(Color.Success, bar.Color);
    }

    [Fact]
    public async Task WhenPageHasExpansionPanelsAndAlert_ThenTheyHaveTheirOptionsAndDefaults()
    {
        // Arrange & Act
        var page = await AddPageAsync("""
            :: HomeBlaze.Components.ExpansionPanels#panels Single=true
            ::: Panel Title="One" Icon=Bed Expanded=true
            First
            ::: Panel Title="Two"
            Second
            ::

            :: HomeBlaze.Components.Alert#plain
            Plain
            ::

            :: HomeBlaze.Components.Alert#warning Severity=Warning Variant=Outlined Icon=Bolt
            Warning
            ::
            """);

        // Assert
        Assert.DoesNotContain("markdown-block-error", MarkdownPageFixture.Html(page.Children));

        var panels = Assert.IsType<ExpansionPanels>(page.Children["panels"]);
        Assert.True(panels.Single);
        Assert.Equal(2, panels.Panels.Length);
        Assert.Equal("One", panels.Panels[0].Title);
        Assert.Equal("Bed", panels.Panels[0].Icon);
        Assert.True(panels.Panels[0].Expanded);
        Assert.NotNull(panels.Panels[0].Content);
        Assert.False(panels.Panels[1].Expanded);

        var plain = Assert.IsType<Alert>(page.Children["plain"]);
        Assert.Equal(Severity.Info, plain.Severity);
        Assert.Equal(Variant.Text, plain.Variant);
        Assert.NotNull(plain.Content);

        var warning = Assert.IsType<Alert>(page.Children["warning"]);
        Assert.Equal(Severity.Warning, warning.Severity);
        Assert.Equal(Variant.Outlined, warning.Variant);
        Assert.Equal("Bolt", warning.Icon);
    }
}
