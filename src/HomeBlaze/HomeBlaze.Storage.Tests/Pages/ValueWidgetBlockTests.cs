using HomeBlaze.Components;
using HomeBlaze.Samples;
using HomeBlaze.Services;
using HomeBlaze.Storage.Files;
using MudBlazor;
using Xunit;

namespace HomeBlaze.Storage.Tests.Pages;

[Collection(MarkdownPageFixture.StorageCollection)]
public class ValueWidgetBlockTests
{
    private static Task<MarkdownPageFixture> CreateFixtureAsync() =>
        MarkdownPageFixture.CreateAsync(services =>
        {
            // The type registry reads the provider on first use, so the assemblies are added before anything is created.
            var typeProvider = (TypeProvider)services
                .Single(service => service.ServiceType == typeof(TypeProvider))
                .ImplementationInstance!;

            typeProvider
                .AddAssembly(typeof(Card).Assembly)
                .AddAssembly(typeof(Motor).Assembly);
        });

    private static async Task<MarkdownFile> AddPageAsync(string content)
    {
        var fixture = await CreateFixtureAsync();
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

            :: HomeBlaze.Components.Progress#bar Path=/a/Speed Min=10 Max=1600 Color=Success
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
        Assert.Equal(0, plainBar.Min);
        Assert.Equal(1, plainBar.Max);
        Assert.Equal(Color.Primary, plainBar.Color);

        var bar = Assert.IsType<Progress>(page.Children["bar"]);
        Assert.Equal(10, bar.Min);
        Assert.Equal(1600, bar.Max);
        Assert.Equal(Color.Success, bar.Color);
    }
}
