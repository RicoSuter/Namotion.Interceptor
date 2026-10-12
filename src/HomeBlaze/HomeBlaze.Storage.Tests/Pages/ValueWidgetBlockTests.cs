using HomeBlaze.Components;
using HomeBlaze.Samples;
using HomeBlaze.Services;
using HomeBlaze.Storage.Files;
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
}
