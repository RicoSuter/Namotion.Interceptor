using HomeBlaze.Abstractions;
using HomeBlaze.Components;
using HomeBlaze.History.Blazor;
using HomeBlaze.Services;
using HomeBlaze.Storage.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace HomeBlaze.Storage.Tests.Pages;

/// <summary>
/// The list slots of the chart and the table: blocks on a page, a list in a configuration file of their own.
/// </summary>
[Collection(MarkdownPageFixture.StorageCollection)]
public class WidgetListSlotConfigurationTests
{
    private const string ChartPage =
        ":: HomeBlaze.History.Blazor.HistoryChart#chart Range=1h\n" +
        "::: Series Path=/Demo/Pump/CurrentSpeed Label=Pump\n" +
        "::: Series Path=/Demo/Pump/Status Label=Status\n" +
        "::\n";

    [Fact]
    public async Task WhenSeriesIsAddedAndTheChartIsSaved_ThenItsBlockIsInsertedAndTheOthersAreKept()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateWithWidgetsAndSamplesAsync();
        var page = await fixture.AddPageAsync("page.md", ChartPage);
        var chart = Assert.IsType<HistoryChart>(((IPathScope)page).TryGetNamedSubject("chart"));
        var kept = chart.Series.ToArray();
        chart.Series = [.. chart.Series, new HistoryChartSeries { Path = "/Demo/Fan/CurrentSpeed", Label = "Fan" }];

        // Act
        var isWritten = await page.WriteConfigurationAsync(chart, CancellationToken.None);
        var written = await fixture.ReadAsync("page.md");
        await page.OnFileChangedAsync(CancellationToken.None);

        // Assert
        Assert.True(isWritten);
        Assert.Equal(3, written.Split('\n').Count(line => line.StartsWith("::: Series ", StringComparison.Ordinal)));
        Assert.Contains("/Demo/Fan/CurrentSpeed", written);
        Assert.Same(chart, ((IPathScope)page).TryGetNamedSubject("chart"));
        Assert.Equal(["/Demo/Pump/CurrentSpeed", "/Demo/Pump/Status", "/Demo/Fan/CurrentSpeed"], chart.Series.Select(series => series.Path));
        Assert.Same(kept[0], chart.Series[0]);
        Assert.Same(kept[1], chart.Series[1]);
    }

    [Fact]
    public async Task WhenSeriesIsRemovedAndTheChartIsSaved_ThenItsBlockIsRemoved()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateWithWidgetsAndSamplesAsync();
        var page = await fixture.AddPageAsync("page.md", ChartPage);
        var chart = Assert.IsType<HistoryChart>(((IPathScope)page).TryGetNamedSubject("chart"));
        var kept = chart.Series[0];
        chart.Series = [kept];

        // Act
        var isWritten = await page.WriteConfigurationAsync(chart, CancellationToken.None);
        var written = await fixture.ReadAsync("page.md");
        await page.OnFileChangedAsync(CancellationToken.None);

        // Assert
        Assert.True(isWritten);
        Assert.DoesNotContain("/Demo/Pump/Status", written);
        Assert.Same(kept, Assert.Single(chart.Series));
    }

    [Fact]
    public async Task WhenChartArgumentIsChangedAndTheChartIsSaved_ThenOnlyItsHeadLineChangesAndTheSeriesStayBlocks()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateWithWidgetsAndSamplesAsync();
        var page = await fixture.AddPageAsync("page.md", ChartPage);
        var chart = Assert.IsType<HistoryChart>(((IPathScope)page).TryGetNamedSubject("chart"));
        chart.Range = "6h";

        // Act
        var isWritten = await page.WriteConfigurationAsync(chart, CancellationToken.None);
        var written = await fixture.ReadAsync("page.md");

        // Assert
        Assert.True(isWritten);
        Assert.Equal(ChartPage.Replace("Range=1h", "Range=6h", StringComparison.Ordinal), written);
    }

    [Fact]
    public async Task WhenColumnIsAddedAndTheTableIsSaved_ThenItsBlockIsInserted()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateWithWidgetsAndSamplesAsync();
        var page = await fixture.AddPageAsync(
            "page.md", ":: HomeBlaze.Components.SubjectTable#table Source=..\n::: Column Property=Name\n::\n");
        var table = Assert.IsType<SubjectTable>(((IPathScope)page).TryGetNamedSubject("table"));
        table.Columns = [.. table.Columns, new SubjectTableColumn { Property = "Status" }];

        // Act
        var isWritten = await page.WriteConfigurationAsync(table, CancellationToken.None);
        var written = await fixture.ReadAsync("page.md");

        // Assert
        Assert.True(isWritten);
        Assert.Equal(
            ":: HomeBlaze.Components.SubjectTable#table Source=..\n::: Column Property=Name\n::: Column Property=Status\n::\n",
            written);
    }

    [Fact]
    public void WhenNewChartAndTableAreWrittenAsBlocks_ThenTheirEmptyListsAreLeftOut()
    {
        // Arrange
        var serializer = CreateSerializer();

        // Act
        var chartJson = MarkdownBlockConfiguration.Serialize(serializer, new HistoryChart { Range = "6h" }, null);
        var tableJson = MarkdownBlockConfiguration.Serialize(serializer, new SubjectTable { Source = "/Demo" }, null);

        // Assert
        Assert.DoesNotContain("Series", chartJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Columns", tableJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WhenHistoryChartIsWrittenToAConfigurationFile_ThenItsSeriesAreReadBack()
    {
        // Arrange
        var serializer = CreateSerializer();
        var chart = new HistoryChart
        {
            Range = "7d",
            Series =
            [
                new HistoryChartSeries { Path = "/Demo/Pump/CurrentSpeed", Label = "Pump", Aggregation = "Maximum" },
                new HistoryChartSeries { Path = "/Demo/Pump/Status", Color = "#ff0000" }
            ]
        };

        // Act
        var json = serializer.Serialize(chart);
        var read = Assert.IsType<HistoryChart>(serializer.Deserialize(json));

        // Assert
        Assert.Contains("\"series\"", json);
        Assert.Equal("7d", read.Range);
        Assert.Equal(
            new (string?, string?, string?, string?)[]
            {
                ("/Demo/Pump/CurrentSpeed", "Pump", null, "Maximum"),
                ("/Demo/Pump/Status", null, "#ff0000", null)
            },
            read.Series.Select(series => (series.Path, series.Label, series.Color, series.Aggregation)));
    }

    [Fact]
    public void WhenSubjectTableIsWrittenToAConfigurationFile_ThenItsColumnsAreReadBack()
    {
        // Arrange
        var serializer = CreateSerializer();
        var table = new SubjectTable
        {
            Source = "/Demo",
            Columns = [new SubjectTableColumn { Property = "Name" }, new SubjectTableColumn { Property = "Status", Label = "State", Map = "motorstate" }]
        };

        // Act
        var json = serializer.Serialize(table);
        var read = Assert.IsType<SubjectTable>(serializer.Deserialize(json));

        // Assert
        Assert.Contains("\"columns\"", json);
        Assert.Equal("/Demo", read.Source);
        Assert.Equal(
            new (string?, string?, string?)[] { ("Name", null, null), ("Status", "State", "motorstate") },
            read.Columns.Select(column => (column.Property, column.Label, column.Map)));
    }

    private static ConfigurableSubjectSerializer CreateSerializer()
    {
        var typeProvider = new TypeProvider();
        typeProvider.AddTypes([typeof(HistoryChart), typeof(HistoryChartSeries), typeof(SubjectTable), typeof(SubjectTableColumn)]);
        return new ConfigurableSubjectSerializer(typeProvider, new ServiceCollection().BuildServiceProvider());
    }
}
