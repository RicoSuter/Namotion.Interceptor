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
