using System.Reflection;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.History;
using HomeBlaze.History.Abstractions;
using HomeBlaze.History.Blazor;
using HomeBlaze.Services;
using HomeBlaze.Services.Lifecycle;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;
using Xunit;

namespace HomeBlaze.History.Blazor.Tests;

public class HistoryChartSeriesResolverTests
{
    private static readonly IReadOnlySet<string> AllAggregations =
        new HashSet<string>(HistoryAggregations.All, StringComparer.Ordinal);

    private static HistoryChartSeriesInfo Line(
        string label, StateUnit unit = StateUnit.Default, bool hasHistory = true, string? color = null, string? aggregation = null) =>
        new(label, color, null, HistoryChartSeriesKind.Line, unit, hasHistory, IsCumulative: false, aggregation);

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
        Assert.False(chart.Compact);
        Assert.Null(chart.Aggregation);
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
        var error = HistoryChartSeriesResolver.FindArgumentError("24h", "Auto", null, series);

        // Assert
        Assert.Null(error);
    }

    [Fact]
    public void WhenALineHasAnotherUnitThanTheFirstLine_ThenTheErrorNamesBoth()
    {
        // Arrange
        HistoryChartSeriesInfo[] series = [Band("Status"), Line("Pump"), Line("Temperature", StateUnit.DegreeCelsius)];

        // Act
        var error = HistoryChartSeriesResolver.FindArgumentError("24h", "Auto", null, series);

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
        var error = HistoryChartSeriesResolver.FindArgumentError("24h", "Auto", null, series);

        // Assert
        Assert.Null(error);
    }

    [Theory]
    [InlineData("2h", "Auto", "Unknown range '2h'")]
    [InlineData("24h", "2m", "Unknown bucket '2m'")]
    public void WhenRangeOrBucketIsUnknown_ThenTheErrorNamesTheValueAndTheChoices(string range, string bucket, string expectedStart)
    {
        // Act
        var error = HistoryChartSeriesResolver.FindArgumentError(range, bucket, null, []);

        // Assert
        Assert.NotNull(error);
        Assert.StartsWith(expectedStart, error);
    }

    [Fact]
    public void WhenRangeAndBucketAreKnownInAnotherCase_ThenThereIsNoError()
    {
        // Act
        var error = HistoryChartSeriesResolver.FindArgumentError("7D", "raw", null, []);

        // Assert
        Assert.Null(error);
    }

    [Fact]
    public void WhenTheAggregationOfTheChartIsUnknown_ThenTheErrorNamesTheValueAndTheChoices()
    {
        // Act
        var error = HistoryChartSeriesResolver.FindArgumentError("24h", "Auto", "Median", [Line("Pump", aggregation: "Median")]);

        // Assert
        Assert.Equal(
            "Unknown aggregation 'Median': use one of TimeWeightedAverage, Average, SampleAverage, Minimum, Maximum, Sum, " +
            "StandardDeviation, Last, First, Count",
            error);
    }

    [Fact]
    public void WhenTheAggregationOfASeriesIsUnknown_ThenTheErrorNamesTheSeries()
    {
        // Act
        var error = HistoryChartSeriesResolver.FindArgumentError(
            "24h", "Auto", "Maximum", [Line("Pump", aggregation: "Maximum"), Line("Fan", aggregation: "Mean")]);

        // Assert
        Assert.NotNull(error);
        Assert.StartsWith("Unknown aggregation 'Mean' of series 'Fan'", error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("maximum")]
    [InlineData("TIMEWEIGHTEDAVERAGE")]
    [InlineData("average")]
    public void WhenTheAggregationIsEmptyOrKnownInAnyCase_ThenThereIsNoError(string? aggregation)
    {
        // Act
        var error = HistoryChartSeriesResolver.FindArgumentError("24h", "Auto", aggregation, [Line("Pump", aggregation: aggregation)]);

        // Assert
        Assert.Null(error);
    }

    [Fact]
    public void WhenALineCountsSamplesNextToALineWithAUnit_ThenTheErrorCallsItACount()
    {
        // Arrange
        HistoryChartSeriesInfo[] series =
        [
            Line("Temperature", StateUnit.DegreeCelsius),
            Line("Samples", StateUnit.DegreeCelsius, aggregation: "count")
        ];

        // Act
        var error = HistoryChartSeriesResolver.FindArgumentError("24h", "Auto", null, series);

        // Assert
        Assert.Equal("Series 'Samples' (count) has another unit than 'Temperature' (°C): show it in a second chart", error);
    }

    [Theory]
    [InlineData("Auto", StateUnit.Default)]
    [InlineData("Raw", StateUnit.DegreeCelsius)]
    public void WhenALineCountsSamplesInBucketsOrIsQueriedRaw_ThenItSharesTheChartWithLinesOfTheMatchingUnit(
        string bucket, StateUnit otherUnit)
    {
        // Arrange
        HistoryChartSeriesInfo[] series =
        [
            Line("Samples", StateUnit.DegreeCelsius, aggregation: HistoryAggregations.Count),
            Line("Other", otherUnit)
        ];

        // Act
        var error = HistoryChartSeriesResolver.FindArgumentError("24h", bucket, null, series);

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

    [Fact]
    public void WhenTheFirstRecordedLineIsSearched_ThenBandsMissingSeriesAndLinesWithoutHistoryAreSkipped()
    {
        // Arrange
        HistoryChartSeriesInfo[] series =
        [
            Band("Status"),
            new("Gone", null, null, HistoryChartSeriesKind.Missing, StateUnit.Default, HasHistory: false, IsCumulative: false),
            Line("Unrecorded", hasHistory: false),
            Line("Fan"),
            Line("Pump")
        ];

        // Act
        var index = HistoryChartSeriesResolver.FindFirstRecordedLine(series);

        // Assert
        Assert.Equal(3, index);
    }

    [Fact]
    public void WhenAChartHasNoRecordedLine_ThenNoLineIsFound()
    {
        // Arrange
        HistoryChartSeriesInfo[] series = [Band("Status"), Line("Unrecorded", hasHistory: false)];

        // Act
        var index = HistoryChartSeriesResolver.FindFirstRecordedLine(series);

        // Assert
        Assert.Equal(-1, index);
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

    [Theory]
    [InlineData(typeof(double), false, false, false, "maximum", HistoryAggregations.Maximum)]
    [InlineData(typeof(double), false, false, false, "", HistoryAggregations.TimeWeightedAverage)]
    [InlineData(typeof(double), false, true, false, HistoryAggregations.Sum, HistoryAggregations.Last)]
    [InlineData(typeof(double), false, true, false, HistoryAggregations.Maximum, HistoryAggregations.Maximum)]
    [InlineData(typeof(double), true, false, false, HistoryAggregations.Maximum, HistoryAggregations.Last)]
    [InlineData(typeof(double), false, false, true, HistoryAggregations.Maximum, HistoryAggregations.Last)]
    public void WhenAnAggregationIsRequested_ThenALineTakesItWhenOfferedAndBandsAndRawQueriesTakeTheLastValue(
        Type propertyType, bool isBand, bool isCumulative, bool isRaw, string requested, string expected)
    {
        // Act
        var aggregation = HistoryChartSeriesResolver.ChooseAggregation(
            propertyType, isBand, isCumulative, isRaw, AllAggregations, requested);

        // Assert
        Assert.Equal(expected, aggregation);
    }

    [Fact]
    public void WhenNoStoreOffersTheRequestedAggregation_ThenALineFallsBackAndItsNoteSaysSo()
    {
        // Arrange
        var storeAggregations = new HashSet<string>(StringComparer.Ordinal) { HistoryAggregations.TimeWeightedAverage };
        var series = Line("Pump", aggregation: "sum");

        // Act
        var aggregation = HistoryChartSeriesResolver.ChooseAggregation(
            typeof(double), false, false, false, storeAggregations, series.Aggregation);
        var note = HistoryChartSeriesResolver.GetAggregationNote(series, isRaw: false, aggregation);

        // Assert
        Assert.Equal(HistoryAggregations.TimeWeightedAverage, aggregation);
        Assert.Equal("Sum not available, shows Average", note);
    }

    [Theory]
    [InlineData(HistoryChartSeriesKind.Line, "maximum", false, HistoryAggregations.Maximum)]
    [InlineData(HistoryChartSeriesKind.Line, "average", false, HistoryAggregations.TimeWeightedAverage)]
    [InlineData(HistoryChartSeriesKind.Line, "maximum", true, HistoryAggregations.Last)]
    [InlineData(HistoryChartSeriesKind.Band, "maximum", false, HistoryAggregations.Last)]
    [InlineData(HistoryChartSeriesKind.Line, null, false, HistoryAggregations.TimeWeightedAverage)]
    public void WhenTheRequestedAggregationIsShownOrDoesNotApply_ThenThereIsNoNote(
        HistoryChartSeriesKind kind, string? requested, bool isRaw, string chosen)
    {
        // Arrange
        var series = new HistoryChartSeriesInfo("Pump", null, null, kind, StateUnit.Default, true, false, requested);

        // Act
        var note = HistoryChartSeriesResolver.GetAggregationNote(series, isRaw, chosen);

        // Assert
        Assert.Null(note);
    }

    [Fact]
    public void WhenTheChartOrASeriesSetsAnAggregation_ThenTheSeriesAsksForItsOwnOrElseTheOneOfTheChart()
    {
        // Arrange
        var (resolver, root) = CreateGraph(
            new HistoryChartSeries { Path = "/Temperature" },
            new HistoryChartSeries { Path = "/Temperature", Aggregation = "Minimum" },
            new HistoryChartSeries { Path = "/Temperature", Aggregation = "" });
        root.Chart!.Aggregation = "Maximum";

        // Act
        var series = HistoryChartSeriesResolver.Resolve(resolver, root.Chart);

        // Assert
        Assert.Equal(["Maximum", "Minimum", "Maximum"], series.Select(entry => entry.Aggregation));
    }

    [Fact]
    public void WhenTheDialogSettingsOfALineAreBuilt_ThenTheyHoldTheRangeTheBucketAndTheAggregationOfTheChart()
    {
        // Arrange
        var (resolver, root) = CreateGraph(new HistoryChartSeries { Path = "/Temperature", Aggregation = "maximum" });
        var series = HistoryChartSeriesResolver.Resolve(resolver, root.Chart!)[0];

        // Act
        var settings = HistoryChartSeriesResolver.GetDialogSettings(series, "6h", "10m");

        // Assert
        PropertyHistoryChartModel.TryGetPeriod("10m", out var period);
        Assert.Equal(new PropertyHistoryDialogSettings(TimeSpan.FromHours(6), period, "maximum"), settings);
    }

    [Fact]
    public void WhenTheDialogSettingsOfALineWithoutAnAggregationAreBuilt_ThenTheDialogChoosesItsDefault()
    {
        // Arrange
        var (resolver, root) = CreateGraph(new HistoryChartSeries { Path = "/Temperature", Aggregation = "" });
        var series = HistoryChartSeriesResolver.Resolve(resolver, root.Chart!)[0];

        // Act
        var settings = HistoryChartSeriesResolver.GetDialogSettings(series, "24h", "Raw");

        // Assert
        Assert.True(settings.Period!.Value.IsRaw);
        Assert.Null(settings.Aggregation);
    }

    [Fact]
    public void WhenTheDialogSettingsOfABandAreBuilt_ThenTheyHoldOnlyTheRange()
    {
        // Arrange
        var (resolver, root) = CreateGraph(new HistoryChartSeries { Path = "/IsOn", Aggregation = "Count" });
        var series = HistoryChartSeriesResolver.Resolve(resolver, root.Chart!)[0];

        // Act
        var settings = HistoryChartSeriesResolver.GetDialogSettings(series, "7d", "10m");

        // Assert
        Assert.Equal(new PropertyHistoryDialogSettings(TimeSpan.FromDays(7)), settings);
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
    public void WhenAStringAnEnumOrADiscreteNumberIsResolved_ThenItIsABand()
    {
        // Arrange
        var (resolver, root) = CreateGraph(
            new HistoryChartSeries { Path = "/Status" },
            new HistoryChartSeries { Path = "/Mode" },
            new HistoryChartSeries { Path = "/Level" });

        // Act
        var series = HistoryChartSeriesResolver.Resolve(resolver, root.Chart!);

        // Assert
        Assert.All(series, entry => Assert.Equal(HistoryChartSeriesKind.Band, entry.Kind));
        Assert.All(series, entry => Assert.True(entry.HasHistory));
    }

    [Fact]
    public void WhenAPathIsRelativeToTheScopeOfTheChart_ThenItResolvesFromThatScope()
    {
        // Arrange
        var (resolver, root) = CreateGraph();
        var chart = new HistoryChart { Series = [new HistoryChartSeries { Path = "../Temperature" }] };
        root.Page = new ChartPageSubject { Chart = chart };

        // Act
        var series = HistoryChartSeriesResolver.Resolve(resolver, chart);

        // Assert
        var entry = Assert.Single(series);
        Assert.Equal(HistoryChartSeriesKind.Line, entry.Kind);
        Assert.Same(root, entry.Property!.Subject);
        Assert.Equal(nameof(ChartTargetSubject.Temperature), entry.Property.Name);
    }

    [Fact]
    public async Task WhenTheHistoryPathIsBuilt_ThenItIsThePathTheRecorderWrites()
    {
        // Arrange
        var (resolver, root) = CreateGraph();
        root.Child = new ChartTargetSubject();
        RegisteredSubjectProperty[] properties =
        [
            root.TryGetRegisteredSubject()!.TryGetProperty(nameof(ChartTargetSubject.Temperature))!,
            root.Child.TryGetRegisteredSubject()!.TryGetProperty(nameof(ChartTargetSubject.Temperature))!
        ];
        var engine = new PathRecordingEngine();
        var recorder = new HistoryChangeRecorder(engine, resolver);
        var changes = properties
            .Select(property => SubjectPropertyChange.Create(
                property.Reference, ChangeOrigin.Local, DateTimeOffset.UnixEpoch, null, 0.0, 1.0))
            .ToArray();

        // Act
        var paths = properties.Select(property => property.GetHistoryPath(resolver)).ToArray();
        await recorder.RecordBatch(changes);

        // Assert
        Assert.Equal(engine.Paths, paths);
        Assert.Equal("/Child/Temperature", paths[1]);
    }

    private static (SubjectPathResolver Resolver, ChartTargetSubject Root) CreateGraph(params HistoryChartSeries[] series)
    {
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithParents()
            .WithLifecycle()
            .WithService<IPropertyLifecycleHandler>(
                () => new PropertyAttributeInitializer(),
                handler => handler is PropertyAttributeInitializer);

        var root = new ChartTargetSubject(context) { Chart = new HistoryChart { Series = series } };
        return (new SubjectPathResolver(() => root), root);
    }

    private sealed class PathRecordingEngine : IHistoryRecorder
    {
        public List<string> Paths { get; } = [];

        public bool TryRecord(string propertyPath, DateTimeOffset timestamp, object? value, Type propertyType)
        {
            Paths.Add(propertyPath);
            return true;
        }

        public void RecordMove(DateTimeOffset timestamp, string fromPath, string toPath)
        {
        }
    }
}
