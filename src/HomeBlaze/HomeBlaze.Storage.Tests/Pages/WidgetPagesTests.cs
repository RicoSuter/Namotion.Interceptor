using HomeBlaze.Abstractions;
using HomeBlaze.Components;
using HomeBlaze.Samples;
using HomeBlaze.Services;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Internal;
using HomeBlaze.Storage.Pages;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Tests.Pages;

/// <summary>
/// Loads the folder of the widget reference as the application does and checks that each widget page shows what its
/// samples promise, with the two shared sample motors of the folder.
/// </summary>
[Collection(MarkdownPageFixture.StorageCollection)]
public class WidgetPagesTests
{
    // Relative to DocumentationFolderFixture.DocumentationFolder, as DocumentationFolderFixture.LoadAsync takes it.
    private const string WidgetsFolder = "administration/widgets";
    private const string PumpPath = "../Pump";
    private const string FanPath = "../Fan";
    private const string MapPath = "../value-map.md/motorstate";

    [Fact]
    public async Task WhenValueMapPageIsLoaded_ThenTheMapHasACaseWithTextPerMotorStatus()
    {
        // Arrange
        using var fixture = await DocumentationFolderFixture.LoadAsync(WidgetsFolder);

        // Act
        IPathScope page = fixture.FindPage(GetPagePath("value-map.md"));

        // Assert
        var map = Assert.IsType<ValueMap>(page.TryGetNamedSubject("motorstate"));
        Assert.Equal(Enum.GetNames<MotorStatus>(), map.Cases.Select(valueCase => valueCase.Value));
        Assert.All(map.Cases, valueCase => Assert.False(string.IsNullOrEmpty(valueCase.Text)));
    }

    [Theory]
    [InlineData("value-list.md")]
    [InlineData("value-tile.md")]
    [InlineData("indicator.md")]
    public async Task WhenValueWidgetPageIsLoaded_ThenEveryMapIsTheMapOfTheValueMapPage(string fileName)
    {
        // Arrange
        using var fixture = await DocumentationFolderFixture.LoadAsync(WidgetsFolder);
        var map = Assert.IsType<ValueMap>(((IPathScope)fixture.FindPage(GetPagePath("value-map.md"))).TryGetNamedSubject("motorstate"));
        var resolver = fixture.Services.GetRequiredService<SubjectPathResolver>();

        // Act
        var widgets = DocumentationFolderFixture.FindSubjects(fixture.FindPage(GetPagePath(fileName)))
            .Select(subject => (Subject: subject, Map: subject switch
            {
                ValueRow row => row.Map,
                ValueTile tile => tile.Map,
                Indicator indicator => indicator.Map,
                _ => null
            }))
            .Where(widget => widget.Map is not null)
            .ToList();

        // Assert
        Assert.NotEmpty(widgets);
        Assert.All(widgets, widget => Assert.Equal(MapPath, widget.Map));
        Assert.All(widgets, widget => Assert.Same(map, resolver.ResolveScopedSubject(widget.Map!, PathStyle.Canonical, widget.Subject)));
    }

    [Fact]
    public async Task WhenValueWidgetPagesAreLoaded_ThenEachHasTheSamplesOfItsSection()
    {
        // Arrange
        using var fixture = await DocumentationFolderFixture.LoadAsync(WidgetsFolder);

        // Act
        var valueList = DocumentationFolderFixture.FindSubjects(fixture.FindPage(GetPagePath("value-list.md")));
        var valueTile = DocumentationFolderFixture.FindSubjects(fixture.FindPage(GetPagePath("value-tile.md")));
        var indicator = DocumentationFolderFixture.FindSubjects(fixture.FindPage(GetPagePath("indicator.md")));
        var progress = DocumentationFolderFixture.FindSubjects(fixture.FindPage(GetPagePath("progress.md")));

        // Assert
        Assert.Equal((3, 1), (valueList.OfType<ValueList>().Count(), valueList.OfType<Progress>().Count()));
        Assert.Equal(3, valueTile.OfType<ValueTile>().Count());
        Assert.Equal([(140, 3)], valueTile.OfType<FlowLayout>().Select(flow => (flow.MinWidth, flow.Items.Length)));
        Assert.Equal([IndicatorKind.Chip, IndicatorKind.Icon, IndicatorKind.Icon, IndicatorKind.Chip], indicator.OfType<Indicator>().Select(widget => widget.Kind));
        Assert.Single(indicator.OfType<Card>());
        Assert.Equal(2, progress.OfType<Progress>().Count());
    }

    [Theory]
    [InlineData("value-list.md")]
    [InlineData("value-tile.md")]
    [InlineData("indicator.md")]
    [InlineData("progress.md")]
    public async Task WhenValueWidgetPageIsLoaded_ThenEveryValueWidgetReadsAPropertyOfASharedMotor(string fileName)
    {
        // Arrange
        using var fixture = await DocumentationFolderFixture.LoadAsync(WidgetsFolder);
        var resolver = fixture.Services.GetRequiredService<SubjectPathResolver>();

        // Act
        var page = fixture.FindPage(GetPagePath(fileName));

        // Assert
        var subjects = DocumentationFolderFixture.FindSubjects(page);
        var widgetPaths = subjects.OfType<ValueRow>().Select(row => (Subject: (IInterceptorSubject)row, row.Path))
            .Concat(subjects.OfType<ValueTile>().Select(tile => (Subject: (IInterceptorSubject)tile, tile.Path)))
            .Concat(subjects.OfType<Indicator>().Select(widget => (Subject: (IInterceptorSubject)widget, widget.Path)))
            .Concat(subjects.OfType<Progress>().Select(bar => (Subject: (IInterceptorSubject)bar, bar.Path)))
            .Where(widget => widget.Path is not null)
            .ToList();
        Assert.NotEmpty(widgetPaths);
        Assert.All(widgetPaths, widget => Assert.True(
            widget.Path!.StartsWith(PumpPath + "/", StringComparison.Ordinal) || widget.Path.StartsWith(FanPath + "/", StringComparison.Ordinal),
            widget.Path));
        Assert.All(widgetPaths, widget => Assert.NotNull(resolver.ResolveScopedProperty(widget.Path!, PathStyle.Canonical, widget.Subject)));
    }

    // The path of a page of the folder in the storage, for example /administration/widgets/value-map.md.
    private static string GetPagePath(string fileName) => $"/{WidgetsFolder}/{fileName}";
}
