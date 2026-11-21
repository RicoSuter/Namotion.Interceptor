using HomeBlaze.E2E.Tests.Infrastructure;
using HomeBlaze.E2E.Tests.Models;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for the panes of the subject browser while a subject they show is replaced or moved in the graph.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class SubjectBrowserReloadTests
{
    private const string ParentJson =
        """
        {
          "$type": "HomeBlaze.E2E.Tests.Models.NestedConfigurationSubject",
          "child": { "$type": "HomeBlaze.E2E.Tests.Models.NestedConfigurationChild", "label": "Loaded label" }
        }
        """;

    private readonly PlaywrightFixture _fixture;

    public SubjectBrowserReloadTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task WhenFileOfANestedSubjectIsLoadedAgain_ThenItsPaneShowsTheNewSubject()
    {
        await WithStoredSubjectAsync(async (name, filePath) =>
        {
            // Arrange
            var page = await SubjectBrowserPage.OpenAsync(_fixture, $"{name}/Child");
            var panes = page.Locator("#scrollContainer [data-testid='subject-pane']");
            await Assertions.Expect(panes).ToHaveCountAsync(3);
            var label = GetValueText(page, panes.Last, "ShownLabel");
            await Assertions.Expect(label).ToHaveTextAsync("Loaded label");

            // Act: the configuration of the file replaces the child with a new subject.
            await File.WriteAllTextAsync(filePath, ParentJson.Replace("Loaded label", "Reloaded label"));
            await _fixture.ReconcileStorageAsync();

            // Assert
            await Assertions.Expect(label).ToHaveTextAsync("Reloaded label");
            await Assertions.Expect(panes).ToHaveCountAsync(3);
        });
    }

    [Fact]
    public async Task WhenFileTurnsIntoAnotherSubjectWithoutTheShownChild_ThenThePanesOfTheChildClose()
    {
        await WithStoredSubjectAsync(async (name, filePath) =>
        {
            // Arrange
            var page = await SubjectBrowserPage.OpenAsync(_fixture, $"{name}/Child");
            var panes = page.Locator("#scrollContainer [data-testid='subject-pane']");
            await Assertions.Expect(panes).ToHaveCountAsync(3);

            // Act: a subject of another type replaces the subject of the file in the children of the root.
            await File.WriteAllTextAsync(filePath, """{ "$type": "HomeBlaze.Components.GridLayout" }""");
            await _fixture.ReconcileStorageAsync();

            // Assert
            await Assertions.Expect(panes).ToHaveCountAsync(2);
            await Assertions.Expect(panes.Last.GetByRole(AriaRole.Heading)).ToHaveTextAsync("Grid");
            await Assertions.Expect(page).ToHaveURLAsync($"{_fixture.ServerAddress}browser/{name}");
        });
    }

    [Fact]
    public async Task WhenAnItemBeforeTheShownOneIsRemoved_ThenThePaneKeepsShowingItInsteadOfTheItemThatTakesItsIndex()
    {
        // Arrange
        var (name, list) = await _fixture.AddSubjectToRootAsync<ChildListSubject>();
        try
        {
            var items = new[] { "First", "Second", "Third" }
                .Select(label => new NestedConfigurationSubject { Child = new NestedConfigurationChild { Label = $"{label} child" } })
                .ToArray();
            list.Items = items;

            var page = await SubjectBrowserPage.OpenAsync(_fixture, $"{name}/Items/1/Child");
            var panes = page.Locator("#scrollContainer [data-testid='subject-pane']");
            await Assertions.Expect(panes).ToHaveCountAsync(4);
            var label = GetValueText(page, panes.Last, "ShownLabel");
            await Assertions.Expect(label).ToHaveTextAsync("Second child");

            // Act: the shown item moves to index 0 and the third takes index 1. The child of the shown item is replaced
            // afterwards, which only a pane that still shows the item follows.
            list.Items = [items[1], items[2]];
            items[1].Child = new NestedConfigurationChild { Label = "Replaced child" };

            // Assert
            await Assertions.Expect(label).ToHaveTextAsync("Replaced child");
            await Assertions.Expect(panes).ToHaveCountAsync(4);
        }
        finally
        {
            _fixture.RemoveSubjectFromRoot(name, list);
        }
    }

    [Fact]
    public async Task WhenAPendingPathReachesAnotherItemAtItsIndex_ThenItsPanesShowThatItemLikeAReload()
    {
        // Arrange
        var (name, list) = await _fixture.AddSubjectToRootAsync<ChildListSubject>();
        try
        {
            var itemWithoutChild = new NestedConfigurationSubject();
            list.Items = [CreateItem("First child"), itemWithoutChild];

            var page = await SubjectBrowserPage.OpenAsync(_fixture, $"{name}/Items/1/Child");
            var panes = page.Locator("#scrollContainer [data-testid='subject-pane']");
            await Assertions.Expect(panes).ToHaveCountAsync(3);

            // Act: the item of the path moves to index 0, and the item that takes index 1 has the child of the path.
            list.Items = [itemWithoutChild, CreateItem("Third child")];

            // Assert
            await Assertions.Expect(panes).ToHaveCountAsync(4);
            await Assertions.Expect(GetValueText(page, panes.Last, "ShownLabel")).ToHaveTextAsync("Third child");
            await Assertions.Expect(page).ToHaveURLAsync($"{_fixture.ServerAddress}browser/{name}/Items/1/Child");
        }
        finally
        {
            _fixture.RemoveSubjectFromRoot(name, list);
        }
    }

    [Fact]
    public async Task WhenASubjectOfAPendingPathIsRemovedAndAddedAgain_ThenItsPanesFollowAndTheUrlStays()
    {
        // Arrange
        var (name, list) = await _fixture.AddSubjectToRootAsync<ChildListSubject>();
        try
        {
            var firstItem = CreateItem("First child");
            var item = new NestedConfigurationSubject();
            list.Items = [firstItem, item];

            var page = await SubjectBrowserPage.OpenAsync(_fixture, $"{name}/Items/1/Child");
            var panes = page.Locator("#scrollContainer [data-testid='subject-pane']");
            await Assertions.Expect(panes).ToHaveCountAsync(3);

            // Act: the item of the path is removed, and added again with the child of the path.
            list.Items = [firstItem];
            await Assertions.Expect(panes).ToHaveCountAsync(2);
            item.Child = new NestedConfigurationChild { Label = "Added child" };
            list.Items = [firstItem, item];

            // Assert
            await Assertions.Expect(panes).ToHaveCountAsync(4);
            await Assertions.Expect(GetValueText(page, panes.Last, "ShownLabel")).ToHaveTextAsync("Added child");
            await Assertions.Expect(page).ToHaveURLAsync($"{_fixture.ServerAddress}browser/{name}/Items/1/Child");
        }
        finally
        {
            _fixture.RemoveSubjectFromRoot(name, list);
        }
    }

    [Fact]
    public async Task WhenAPendingPathLinksToAProperty_ThenItsRowIsHighlightedOnlyOnceThePathReachesItsSubject()
    {
        // Arrange
        var (name, list) = await _fixture.AddSubjectToRootAsync<ChildListSubject>();
        try
        {
            list.ShownLabel = "List label";

            var page = await SubjectBrowserPage.OpenAsync(_fixture, $"{name}/Items/0/Child?property=ShownLabel");
            var panes = page.Locator("#scrollContainer [data-testid='subject-pane']");
            var highlighted = page.Locator("[data-testid='subject-pane-value'][data-highlighted='true']");
            await Assertions.Expect(panes).ToHaveCountAsync(2);
            await Assertions.Expect(highlighted).ToHaveCountAsync(0, new() { Timeout = AssertionTimeouts.Absence });

            // Act
            list.Items = [CreateItem("Item child")];

            // Assert
            await Assertions.Expect(panes).ToHaveCountAsync(4);
            await Assertions.Expect(highlighted).ToHaveCountAsync(1);
            await Assertions.Expect(panes.Last.Locator("[data-testid='subject-pane-value'][data-highlighted='true']")
                .GetByTestId("subject-pane-value-name")).ToHaveTextAsync("ShownLabel");
        }
        finally
        {
            _fixture.RemoveSubjectFromRoot(name, list);
        }
    }

    private static NestedConfigurationSubject CreateItem(string childLabel)
        => new() { Child = new NestedConfigurationChild { Label = childLabel } };

    private static ILocator GetValueText(IPage page, ILocator pane, string displayName)
        => pane.GetByTestId("subject-pane-value")
            .Filter(new() { Has = page.GetByTestId("subject-pane-value-name").GetByText(displayName, new() { Exact = true }) })
            .GetByTestId("subject-pane-value-text");

    // Stores a subject with a nested subject as a JSON file of the root storage, loads it and deletes it with its file
    // after the test.
    private async Task WithStoredSubjectAsync(Func<string, string, Task> test)
    {
        var name = $"Reload{Guid.NewGuid():N}";
        var filePath = _fixture.GetPagePath($"{name}.json");
        await File.WriteAllTextAsync(filePath, ParentJson);
        try
        {
            await _fixture.ReconcileStorageAsync();
            await test(name, filePath);
        }
        finally
        {
            await _fixture.DeleteStoredSubjectByNameAsync(name);
            File.Delete(filePath);
        }
    }
}
