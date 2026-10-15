using HomeBlaze.History.Blazor;
using HomeBlaze.Storage.Pages;

namespace HomeBlaze.Storage.Tests.Pages;

[Collection(MarkdownPageFixture.StorageCollection)]
public class HistoryChartBlockTests
{
    [Fact]
    public async Task WhenAPageHasAHistoryChartBlock_ThenItCreatesTheChartWithItsSeries()
    {
        // Arrange
        const string source =
            ":: HomeBlaze.Samples.Motor#pump Name=\"Pump\"\n" +
            "::\n\n" +
            ":: HomeBlaze.History.Blazor.HistoryChart Range=1h Bucket=5m Height=200 RefreshInterval=00:05:00\n" +
            "::: Series Path=pump/CurrentSpeed Label=\"Pump\" Color=\"#E4572E\"\n" +
            "::: Series Path=pump/Status\n" +
            "::\n";
        var fixture = await MarkdownPageFixture.CreateWithWidgetsAndSamplesAsync();

        // Act
        var page = await fixture.AddPageAsync("Chart.md", source);

        // Assert
        Assert.Empty(PageErrorBoxes.FindSortedByLine(page));
        var chartBlock = MarkdownPageParser.Parse(source).Blocks[1];
        var chart = Assert.IsType<HistoryChart>(page.GetBlockSubject(chartBlock.Index));
        Assert.Equal("1h", chart.Range);
        Assert.Equal("5m", chart.Bucket);
        Assert.Equal(200, chart.Height);
        Assert.Equal(TimeSpan.FromMinutes(5), chart.RefreshInterval);
        Assert.Equal(["pump/CurrentSpeed", "pump/Status"], chart.Series.Select(series => series.Path));
        Assert.Equal("#E4572E", chart.Series[0].Color);
    }
}
