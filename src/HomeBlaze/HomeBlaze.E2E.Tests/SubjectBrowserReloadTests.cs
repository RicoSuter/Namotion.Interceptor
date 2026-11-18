using HomeBlaze.E2E.Tests.Infrastructure;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for the panes of the subject browser while the file of a subject they show is loaded again.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class SubjectBrowserReloadTests
{
    private const int Timeout = 30000;

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
            await Assertions.Expect(panes).ToHaveCountAsync(3, new() { Timeout = Timeout });
            var label = GetValueText(page, panes.Last, "ShownLabel");
            await Assertions.Expect(label).ToHaveTextAsync("Loaded label", new() { Timeout = Timeout });

            // Act: the configuration of the file replaces the child with a new subject.
            await File.WriteAllTextAsync(filePath, ParentJson.Replace("Loaded label", "Reloaded label"));
            await _fixture.ReconcileStorageAsync();

            // Assert
            await Assertions.Expect(label).ToHaveTextAsync("Reloaded label", new() { Timeout = Timeout });
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
            await Assertions.Expect(panes).ToHaveCountAsync(3, new() { Timeout = Timeout });

            // Act: a subject of another type replaces the subject of the file in the children of the root.
            await File.WriteAllTextAsync(filePath, """{ "$type": "HomeBlaze.Components.GridLayout" }""");
            await _fixture.ReconcileStorageAsync();

            // Assert
            await Assertions.Expect(panes).ToHaveCountAsync(2, new() { Timeout = Timeout });
            await Assertions.Expect(panes.Last.GetByRole(AriaRole.Heading)).ToHaveTextAsync("Grid", new() { Timeout = Timeout });
            await Assertions.Expect(page).ToHaveURLAsync($"{_fixture.ServerAddress}browser/{name}", new() { Timeout = Timeout });
        });
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
