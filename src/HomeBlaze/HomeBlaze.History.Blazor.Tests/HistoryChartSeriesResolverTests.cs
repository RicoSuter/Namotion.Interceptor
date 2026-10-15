using System.Reflection;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.History.Abstractions;
using HomeBlaze.History.Blazor;
using HomeBlaze.Services;
using HomeBlaze.Services.Lifecycle;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;
using Xunit;

namespace HomeBlaze.History.Blazor.Tests;

public class HistoryChartSeriesResolverTests
{
    private static readonly IReadOnlySet<string> AllAggregations =
        new HashSet<string>(HistoryAggregations.All, StringComparer.Ordinal);

    private static HistoryChartSeriesInfo Line(string label, StateUnit unit = StateUnit.Default, bool hasHistory = true, string? color = null) =>
        new(label, color, null, HistoryChartSeriesKind.Line, unit, hasHistory, IsCumulative: false);

    private static HistoryChartSeriesInfo Band(string label, StateUnit unit = StateUnit.Default) =>
        new(label, null, null, HistoryChartSeriesKind.Band, unit, HasHistory: true, IsCumulative: false);

    [Fact]
    public void WhenAChartIsCreated_ThenItHasTheDocumentedDefaults()
    {
        // Act
        var chart = new HistoryChart();

        // Assert
        Assert.Equal("24h", chart.Range);
        Assert.Equal("Auto", chart.Bucket);
        Assert.Equal(300, chart.Height);
        Assert.Equal(TimeSpan.FromSeconds(60), chart.RefreshInterval);
        Assert.Empty(chart.Series);
    }

    [Fact]
    public void WhenTheSeriesPathIsPicked_ThenNumbersBooleansAndEnumsAreOffered()
    {
        // Act
        var attribute = typeof(HistoryChartSeries).GetProperty(nameof(HistoryChartSeries.Path))!
            .GetCustomAttribute<SubjectPathAttribute>()!;

        // Assert
        Assert.Equal(SubjectPathKinds.Number | SubjectPathKinds.Boolean | SubjectPathKinds.Enum, attribute.Kind);
        Assert.False(attribute.Writable);
        Assert.Null(attribute.SubjectType);
    }

    [Fact]
    public void WhenTypesAreOfferedForCreation_ThenTheChartIsOfferedAndASeriesIsNot()
    {
        // Arrange
        var typeProvider = new TypeProvider();
        typeProvider.AddTypes([typeof(HistoryChart), typeof(HistoryChartSeries)]);
        var catalog = new SubjectTypeCatalog(new SubjectTypeRegistry(typeProvider));

        // Act
        var descriptors = catalog.GetCreatableTypes([]);

        // Assert
        Assert.Equal([typeof(HistoryChart)], descriptors.Select(descriptor => descriptor.Type));
    }

    [Fact]
    public void WhenLinesShareAUnit_ThenThereIsNoError()
    {
        // Arrange
        HistoryChartSeriesInfo[] series = [Line("Pump"), Band("Status", StateUnit.DegreeCelsius), Line("Fan")];

        // Act
        var error = HistoryChartSeriesResolver.FindArgumentError("24h", "Auto", series);

        // Assert
        Assert.Null(error);
    }

    [Fact]
    public void WhenALineHasAnotherUnitThanTheFirstLine_ThenTheErrorNamesBoth()
    {
        // Arrange
        HistoryChartSeriesInfo[] series = [Band("Status"), Line("Pump"), Line("Temperature", StateUnit.DegreeCelsius)];

        // Act
        var error = HistoryChartSeriesResolver.FindArgumentError("24h", "Auto", series);

        // Assert
        Assert.NotNull(error);
        Assert.Contains("'Temperature'", error);
        Assert.Contains("'Pump'", error);
        Assert.Contains("second chart", error);
    }

    [Fact]
    public void WhenALineWithoutHistoryHasAnotherUnit_ThenThereIsNoError()
    {
        // Arrange
        HistoryChartSeriesInfo[] series = [Line("Pump"), Line("Temperature", StateUnit.DegreeCelsius, hasHistory: false)];

        // Act
        var error = HistoryChartSeriesResolver.FindArgumentError("24h", "Auto", series);

        // Assert
        Assert.Null(error);
    }

    [Theory]
    [InlineData("2h", "Auto", "Unknown range '2h'")]
    [InlineData("24h", "2m", "Unknown bucket '2m'")]
    public void WhenRangeOrBucketIsUnknown_ThenTheErrorNamesTheValueAndTheChoices(string range, string bucket, string expectedStart)
    {
        // Act
        var error = HistoryChartSeriesResolver.FindArgumentError(range, bucket, []);

        // Assert
        Assert.NotNull(error);
        Assert.StartsWith(expectedStart, error);
    }

    [Fact]
    public void WhenRangeAndBucketAreKnownInAnotherCase_ThenThereIsNoError()
    {
        // Act
        var error = HistoryChartSeriesResolver.FindArgumentError("7D", "raw", []);

        // Assert
        Assert.Null(error);
    }

    [Fact]
    public void WhenLineColorsAreAssigned_ThenOnlyChartedLinesTakeTheNextColorOrTheirOwn()
    {
        // Arrange
        HistoryChartSeriesInfo[] series =
        [
            Line("A"),
            Band("B"),
            Line("C", color: "#123456"),
            Line("D", hasHistory: false),
            Line("E")
        ];

        // Act
        var colors = HistoryChartSeriesResolver.AssignLineColors(series);

        // Assert
        Assert.Equal(["#2979FF", null, "#123456", null, "#F6A609"], colors.AsEnumerable());
    }

    [Theory]
    [InlineData(typeof(double), false, false, false, HistoryAggregations.TimeWeightedAverage)]
    [InlineData(typeof(int), false, false, true, HistoryAggregations.Last)]
    [InlineData(typeof(bool), true, false, false, HistoryAggregations.Last)]
    [InlineData(typeof(double), false, true, false, HistoryAggregations.Last)]
    public void WhenAnAggregationIsChosen_ThenLinesAverageAndEverythingElseTakesTheLastValue(
        Type propertyType, bool isBand, bool isCumulative, bool isRaw, string expected)
    {
        // Act
        var aggregation = HistoryChartSeriesResolver.ChooseAggregation(propertyType, isBand, isCumulative, isRaw, AllAggregations);

        // Assert
        Assert.Equal(expected, aggregation);
    }

    [Fact]
    public void WhenNoStoreAverages_ThenALineTakesTheLastValue()
    {
        // Arrange
        var onlyLast = new HashSet<string>(StringComparer.Ordinal) { HistoryAggregations.Last };

        // Act
        var aggregation = HistoryChartSeriesResolver.ChooseAggregation(typeof(double), false, false, false, onlyLast);

        // Assert
        Assert.Equal(HistoryAggregations.Last, aggregation);
    }

    [Fact]
    public void WhenASeriesLeadsNowhereOrIsNotRecorded_ThenItsNoteSaysSo()
    {
        // Arrange
        var missing = new HistoryChartSeriesInfo("A", null, null, HistoryChartSeriesKind.Missing, StateUnit.Default, false, false);
        var notRecorded = Line("B", hasHistory: false);
        var recorded = Line("C");

        // Act & Assert
        Assert.Equal("not found", missing.Note);
        Assert.Equal("no history recorded", notRecorded.Note);
        Assert.Null(recorded.Note);
    }

    [Fact]
    public void WhenSeriesAreResolved_ThenNumbersAreLinesStatesAreBandsAndUnknownPathsAreMissing()
    {
        // Arrange
        var (resolver, root) = CreateGraph(
            new HistoryChartSeries { Path = "/Temperature" },
            new HistoryChartSeries { Path = "/IsOn", Label = "Power", Color = "#123456" },
            new HistoryChartSeries { Path = "/Energy" },
            new HistoryChartSeries { Path = "/NotRecorded" },
            new HistoryChartSeries { Path = "/Unknown" },
            new HistoryChartSeries());

        // Act
        var series = HistoryChartSeriesResolver.Resolve(resolver, root.Chart!);

        // Assert
        Assert.Equal(
            [
                ("Water temperature", HistoryChartSeriesKind.Line, StateUnit.DegreeCelsius, true, false),
                ("Power", HistoryChartSeriesKind.Band, StateUnit.Default, true, false),
                ("Energy", HistoryChartSeriesKind.Line, StateUnit.Default, true, true),
                ("NotRecorded", HistoryChartSeriesKind.Line, StateUnit.Default, false, false),
                ("/Unknown", HistoryChartSeriesKind.Missing, StateUnit.Default, false, false),
                ("", HistoryChartSeriesKind.Missing, StateUnit.Default, false, false)
            ],
            series.Select(entry => (entry.Label, entry.Kind, entry.Unit, entry.HasHistory, entry.IsCumulative)));
        Assert.Equal("#123456", series[1].Color);
        Assert.Same(root, series[0].Property!.Subject);
        Assert.Null(series[4].Property);
    }

    [Fact]
    public void WhenTheHistoryPathIsBuilt_ThenItIsThePathTheRecorderWrites()
    {
        // Arrange
        var (resolver, root) = CreateGraph();
        root.Child = new ChartTargetSubject();
        var rootProperty = root.TryGetRegisteredSubject()!.TryGetProperty(nameof(ChartTargetSubject.Temperature))!;
        var childProperty = root.Child.TryGetRegisteredSubject()!.TryGetProperty(nameof(ChartTargetSubject.Temperature))!;

        // Act
        var rootPath = HistoryChartSeriesResolver.GetHistoryPath(resolver, rootProperty);
        var childPath = HistoryChartSeriesResolver.GetHistoryPath(resolver, childProperty);

        // Assert
        Assert.Equal("/Temperature", rootPath);
        Assert.Equal("/Child/Temperature", childPath);
    }

    private static (SubjectPathResolver Resolver, ChartTargetSubject Root) CreateGraph(params HistoryChartSeries[] series)
    {
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithService<IPropertyLifecycleHandler>(
                () => new PropertyAttributeInitializer(),
                handler => handler is PropertyAttributeInitializer);

        var root = new ChartTargetSubject(context) { Chart = new HistoryChart { Series = series } };
        return (new SubjectPathResolver(() => root), root);
    }
}
