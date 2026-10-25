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
/// Loads every page of the widget reference with the folder it stands in, as the application does.
/// </summary>
[Collection(MarkdownPageFixture.StorageCollection)]
public class PageWidgetsPageTests
{
    // Relative to DocumentationFolderFixture.DocumentationFolder, as in WidgetPagesTests.
    private const string WidgetsFolder = "administration/widgets";

    // The only path of the pages that leads to nothing, in the sample of widget.md that shows the message for it.
    private const string PathToNothing = "/Missing/Motor";

    public static TheoryData<string> PageNames()
    {
        var data = new TheoryData<string>();
        var folder = Path.Combine(RepositoryPagesTests.FindRepositoryRoot(), DocumentationFolderFixture.DocumentationFolder, WidgetsFolder);
        foreach (var file in Directory.EnumerateFiles(folder, "*.md").Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetFileName(file));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(PageNames))]
    public async Task WhenWidgetPageIsLoaded_ThenNoBlockIsShownAsError(string pageName)
    {
        // Arrange
        using var fixture = await DocumentationFolderFixture.LoadAsync(WidgetsFolder);

        // Act
        var page = fixture.FindPage($"/{WidgetsFolder}/{pageName}");

        // Assert
        Assert.Empty(PageErrorBoxes.FindSortedByLine(page));
    }

    [Fact]
    public async Task WhenWidgetPagesAreLoaded_ThenTheOnlyMotorsAreTheTwoSharedOnes()
    {
        // Act
        using var fixture = await DocumentationFolderFixture.LoadAsync(WidgetsFolder);

        // Assert: a motor is a running background service, so no sample creates one of its own.
        var subjects = fixture.Pages.SelectMany(page => DocumentationFolderFixture.FindSubjects(page)).ToList();
        Assert.Empty(subjects.OfType<Motor>());
        var shown = subjects.OfType<Widget>().Where(widget => widget.Path != PathToNothing).ToList();
        Assert.NotEmpty(shown);
        Assert.All(shown, widget => Assert.Contains(Assert.IsType<Motor>(widget.ResolvedSubject).Name, new[] { "Pump", "Fan" }));
    }

    [Fact]
    public async Task WhenWidgetPagesAreLoaded_ThenOnlyTheDocumentedWidgetLeadsNowhere()
    {
        // Act
        using var fixture = await DocumentationFolderFixture.LoadAsync(WidgetsFolder);

        // Assert
        var widgets = fixture.Pages.SelectMany(page => DocumentationFolderFixture.FindSubjects(page)).OfType<Widget>();
        Assert.Equal([PathToNothing], widgets.Where(widget => widget.ResolvedSubject is null).Select(widget => widget.Path));
    }
}
