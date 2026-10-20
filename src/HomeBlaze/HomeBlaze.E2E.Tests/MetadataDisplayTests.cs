using System.Text.Json;
using HomeBlaze.E2E.Tests.Infrastructure;
using HomeBlaze.Services;
using HomeBlaze.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Namotion.Interceptor.Registry;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for the notes, links and tags of metadata in the subject browser.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class MetadataDisplayTests
{
    private const int PageLoadTimeout = 30000;

    private const string MotorTag = "e2e-floor-2";

    private const string MotorSidecar = $$"""
        {
          "notes": "Calibrated **2026**",
          "tags": ["{{MotorTag}}"],
          "links": [
            { "title": "Manual", "url": "https://example.com/motor.pdf" },
            { "title": "Inline page", "path": "/Demo/Inline.md" },
            { "title": "History values", "path": "/Demo/HistoryValues" },
            { "title": "Temperature", "path": "/Demo/Motor/Temperature" },
            { "path": "/Demo/Gone" },
            { "title": "Script", "url": "javascript:alert(1)" }
          ],
          "$properties": {
            "Temperature": { "notes": "Measured at the housing", "tags": ["{{MotorTag}}"] }
          }
        }
        """;

    private readonly PlaywrightFixture _fixture;

    public MetadataDisplayTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<(IPage Page, ILocator Pane)> OpenPaneAsync(string subjectPath)
    {
        var page = await _fixture.CreatePageAsync();
        await page.SetViewportSizeAsync(1280, 720);
        await page.GotoAsync($"{_fixture.ServerAddress}browser/{subjectPath}");

        // The page is prerendered, and a click before its circuit is interactive is lost.
        await page.WaitUntilInteractiveAsync(PageLoadTimeout);
        return (page, page.Locator("#scrollContainer [data-testid='subject-pane']").Last);
    }

    private static ILocator GetRow(IPage page, ILocator pane, string displayName)
        => pane.GetByTestId("subject-pane-value")
            .Filter(new() { Has = page.GetByTestId("subject-pane-value-name").GetByText(displayName, new() { Exact = true }) });

    // Writes a sidecar in the demo folder of the test data, runs a pass so that its subject has it, and restores it after the test.
    private async Task WithSidecarAsync(string fileName, string content, Func<Task> test)
    {
        var path = _fixture.GetDemoPagePath(fileName);
        var original = File.Exists(path) ? await File.ReadAllTextAsync(path) : null;
        try
        {
            await File.WriteAllTextAsync(path, content);
            await _fixture.ReconcileStorageAsync();
            await test();
        }
        finally
        {
            if (original is null)
            {
                File.Delete(path);
            }
            else
            {
                await File.WriteAllTextAsync(path, original);
            }

            await _fixture.ReconcileStorageAsync();
        }
    }

    // Adds a subject whose state starts without values, stored as a JSON file in the demo folder with the given sidecar
    // beside it, if any, and removes both after the test.
    private async Task WithValuesSubjectAsync(string? sidecar, Func<Task> test)
    {
        var path = _fixture.GetDemoPagePath("MetadataValues.json");
        var sidecarPath = _fixture.GetDemoPagePath("MetadataValues.metadata.json");
        try
        {
            await File.WriteAllTextAsync(path, """{ "$type": "HomeBlaze.E2E.Tests.Models.HistoryValuesSubject" }""");
            if (sidecar is not null)
            {
                await File.WriteAllTextAsync(sidecarPath, sidecar);
            }

            await _fixture.ReconcileStorageAsync();
            await test();
        }
        finally
        {
            File.Delete(sidecarPath);
            File.Delete(path);
            await _fixture.ReconcileStorageAsync();
        }
    }

    [Fact]
    public Task WhenASubjectHasNotesLinksAndTags_ThenThePaneShowsThem() => WithSidecarAsync("Motor.metadata.json", MotorSidecar, async () =>
    {
        // Act
        var (_, pane) = await OpenPaneAsync("Demo/Motor");

        // Assert
        var summary = pane.GetByTestId("metadata-summary");
        await Assertions.Expect(summary.GetByTestId("metadata-notes").Locator("strong")).ToHaveTextAsync("2026", new() { Timeout = PageLoadTimeout });
        await Assertions.Expect(summary.GetByTestId("metadata-tag")).ToHaveTextAsync(MotorTag);

        var links = summary.GetByTestId("metadata-link");
        await Assertions.Expect(links).ToHaveTextAsync(["Manual", "Inline page", "History values", "Temperature", "/Demo/Gone", "Script"]);

        await Assertions.Expect(links.Nth(0)).ToHaveAttributeAsync("href", "https://example.com/motor.pdf");
        await Assertions.Expect(links.Nth(0)).ToHaveAttributeAsync("target", "_blank");
        await Assertions.Expect(links.Nth(0)).ToHaveAttributeAsync("rel", "noopener noreferrer");

        await Assertions.Expect(links.Nth(1)).ToHaveAttributeAsync("href", "pages/Demo/Inline.md");
        await Assertions.Expect(links.Nth(2)).ToHaveAttributeAsync("href", "/browser/Demo/HistoryValues");
        await Assertions.Expect(links.Nth(3)).ToHaveAttributeAsync("href", "/browser/Demo/Motor?property=Temperature");
        foreach (var index in new[] { 1, 2, 3 })
        {
            await Assertions.Expect(links.Nth(index)).Not.ToHaveAttributeAsync("target", "_blank");
        }

        // A path that leads to nothing and an address that may not be opened are broken links without an address.
        foreach (var index in new[] { 4, 5 })
        {
            await Assertions.Expect(links.Nth(index)).ToHaveAttributeAsync("title", "Broken link");
            Assert.Null(await links.Nth(index).GetAttributeAsync("href"));
        }
    });

    [Fact]
    public Task WhenNotesBreakTheRules_ThenThePaneShowsThemAsText() => WithSidecarAsync(
        "Motor.metadata.json", """{ "notes": "Raw <b>bold</b> text" }""", async () =>
    {
        // Act
        var (_, pane) = await OpenPaneAsync("Demo/Motor");

        // Assert
        var notes = pane.GetByTestId("metadata-summary").GetByTestId("metadata-notes");
        await Assertions.Expect(notes).ToHaveTextAsync("Raw <b>bold</b> text", new() { Timeout = PageLoadTimeout });
        await Assertions.Expect(notes.Locator("b")).ToHaveCountAsync(0);
    });

    [Fact]
    public Task WhenALinkToAPropertyWithAValueIsOpened_ThenTheRowOfThePropertyIsHighlighted() => WithSidecarAsync("Motor.metadata.json", MotorSidecar, async () =>
    {
        // Arrange
        var motor = ((VirtualFolder)((FluentStorageContainer)_fixture.ServerServices.GetRequiredService<RootManager>().Root!).Children["Demo"]).Children["Motor"];
        Assert.NotNull(motor.TryGetRegisteredSubject()!.TryGetProperty("Temperature")!.GetValue());
        var (page, pane) = await OpenPaneAsync("Demo/Motor");
        var link = pane.GetByTestId("metadata-summary").GetByTestId("metadata-link").Filter(new() { HasText = "Temperature" });

        // Act
        await link.ClickAsync();

        // Assert
        await page.WaitForURLAsync("**/browser/Demo/Motor?property=Temperature", new() { Timeout = PageLoadTimeout });
        var highlighted = page.Locator("[data-testid='subject-pane-value'][data-highlighted='true']");
        await Assertions.Expect(highlighted).ToHaveCountAsync(1, new() { Timeout = PageLoadTimeout });
        await Assertions.Expect(highlighted.GetByTestId("subject-pane-value-name")).ToHaveTextAsync("Temperature");
    });

    [Theory]
    [InlineData(null)]
    [InlineData("""{ "$properties": { "Fraction": { "tags": ["e2e-fraction"] } } }""")]
    public Task WhenALinkLeadsToAPropertyWithoutAValue_ThenItsRowIsHighlightedWithItsMetadataIcon(string? sidecar) => WithValuesSubjectAsync(sidecar, async () =>
    {
        // Act
        var (page, pane) = await OpenPaneAsync("Demo/MetadataValues?property=Fraction");

        // Assert
        var highlighted = page.Locator("[data-testid='subject-pane-value'][data-highlighted='true']");
        await Assertions.Expect(highlighted).ToHaveCountAsync(1, new() { Timeout = PageLoadTimeout });
        await Assertions.Expect(highlighted.GetByTestId("subject-pane-value-name")).ToHaveTextAsync("Fraction");
        await Assertions.Expect(highlighted.GetByTestId("subject-pane-value-metadata")).ToHaveCountAsync(1);
        await Assertions.Expect(pane.GetByTestId("subject-pane-empty-values")).Not.ToContainTextAsync("Fraction");
    });

    [Fact]
    public Task WhenATagIsClicked_ThenTheTagFilterListsTheSubjectsAndPropertiesWithTheTag() => WithSidecarAsync("Motor.metadata.json", MotorSidecar, async () =>
    {
        // Arrange
        var (page, pane) = await OpenPaneAsync("Demo/Motor");

        // Act
        await pane.GetByTestId("metadata-summary").GetByTestId("metadata-tag").ClickAsync();

        // Assert
        var items = page.GetByTestId("metadata-tag-filter-item");
        await Assertions.Expect(items).ToHaveCountAsync(2, new() { Timeout = PageLoadTimeout });
        await Assertions.Expect(items.Nth(0)).ToContainTextAsync("/Demo/Motor");
        await Assertions.Expect(items.Nth(0)).Not.ToContainTextAsync("/Demo/Motor/Temperature");
        await Assertions.Expect(items.Nth(1)).ToContainTextAsync("/Demo/Motor/Temperature");
    });

    [Fact]
    public Task WhenTheMetadataIconOfAPropertyIsHovered_ThenItsTooltipShowsItsNotesAndTags() => WithSidecarAsync("Motor.metadata.json", MotorSidecar, async () =>
    {
        // Arrange
        var (page, pane) = await OpenPaneAsync("Demo/Motor");
        var icon = GetRow(page, pane, "Temperature").GetByTestId("subject-pane-value-metadata");

        // Act
        await icon.HoverAsync();

        // Assert
        var tooltip = page.Locator(".mud-popover-open").GetByTestId("metadata-summary");
        await Assertions.Expect(tooltip.GetByTestId("metadata-notes")).ToHaveTextAsync("Measured at the housing", new() { Timeout = PageLoadTimeout });
        await Assertions.Expect(tooltip.GetByTestId("metadata-tag")).ToHaveTextAsync(MotorTag);
    });

    [Fact]
    public Task WhenATagIsAddedInTheDialogOfAProperty_ThenThePropertiesEntryIsWrittenAndTheRowShowsIt() => WithSidecarAsync("Motor.metadata.json", "{}", async () =>
    {
        // Arrange
        var (page, pane) = await OpenPaneAsync("Demo/Motor");
        var row = GetRow(page, pane, "Temperature");
        await row.HoverAsync();
        await row.GetByTestId("subject-pane-value-metadata").ClickAsync();

        // MudDialog puts its test id on its content, which does not hold the actions.
        var dialog = page.Locator(".mud-dialog").Filter(new() { Has = page.GetByTestId("metadata-edit-dialog") });
        await dialog.GetByTestId("metadata-notes-input").FillAsync("Measured at the housing");
        await dialog.GetByTestId("metadata-tag-input").FillAsync("e2e-sensor");
        await dialog.GetByTestId("metadata-tag-input").PressAsync("Enter");
        await Assertions.Expect(dialog.GetByTestId("metadata-tag-chip")).ToHaveTextAsync("e2e-sensor", new() { Timeout = PageLoadTimeout });

        // Act
        await dialog.GetByTestId("metadata-save").ClickAsync();

        // Assert
        await Assertions.Expect(dialog).ToHaveCountAsync(0, new() { Timeout = PageLoadTimeout });
        var text = await _fixture.WaitForDemoPageTextAsync("Motor.metadata.json", text => text.Contains("e2e-sensor"), "The tag was not written");
        using var sidecar = JsonDocument.Parse(text);
        var entry = sidecar.RootElement.GetProperty("$properties").GetProperty("Temperature");
        Assert.Equal("Measured at the housing", entry.GetProperty("notes").GetString());
        Assert.Equal("e2e-sensor", entry.GetProperty("tags")[0].GetString());
        Assert.False(sidecar.RootElement.TryGetProperty("notes", out _), "The notes of the property were written to the subject.");

        await row.GetByTestId("subject-pane-value-metadata").HoverAsync();
        await Assertions.Expect(page.Locator(".mud-popover-open").GetByTestId("metadata-tag"))
            .ToHaveTextAsync("e2e-sensor", new() { Timeout = PageLoadTimeout });
    });

    [Fact]
    public Task WhenTagsAreTypedAndAddedWithEnter_ThenEachIsOneChipAndARemovedChipIsNotWritten() => WithSidecarAsync("Motor.metadata.json", "{}", async () =>
    {
        // Arrange
        var (page, pane) = await OpenPaneAsync("Demo/Motor");
        await pane.GetByTestId("edit-metadata-button").ClickAsync();

        // MudDialog puts its test id on its content, which does not hold the actions.
        var dialog = page.Locator(".mud-dialog").Filter(new() { Has = page.GetByTestId("metadata-edit-dialog") });
        var tagInput = dialog.GetByTestId("metadata-tag-input");
        await tagInput.PressSequentiallyAsync("e2e-kept");
        await tagInput.PressAsync("Enter");
        await Assertions.Expect(tagInput).ToHaveValueAsync("", new() { Timeout = PageLoadTimeout });
        await tagInput.PressSequentiallyAsync("e2e-removed");
        await tagInput.PressAsync("Enter");
        var chips = dialog.GetByTestId("metadata-tag-chip");
        await Assertions.Expect(chips).ToHaveTextAsync(["e2e-kept", "e2e-removed"], new() { Timeout = PageLoadTimeout });
        await Assertions.Expect(tagInput).ToHaveValueAsync("");

        // Act
        await chips.Filter(new() { HasText = "e2e-removed" }).Locator(".mud-chip-close-button").ClickAsync();
        await Assertions.Expect(chips).ToHaveTextAsync(["e2e-kept"]);
        await dialog.GetByTestId("metadata-save").ClickAsync();

        // Assert
        await Assertions.Expect(dialog).ToHaveCountAsync(0, new() { Timeout = PageLoadTimeout });
        var text = await _fixture.WaitForDemoPageTextAsync("Motor.metadata.json", text => text.Contains("e2e-kept"), "The tag was not written");
        using var sidecar = JsonDocument.Parse(text);
        Assert.Equal(["e2e-kept"], sidecar.RootElement.GetProperty("tags").EnumerateArray().Select(tag => tag.GetString()));
    });

    [Fact]
    public Task WhenAnAgentWritesTheFirstMetadataWhileThePaneIsOpen_ThenThePaneShowsIt() => WithValuesSubjectAsync(null, async () =>
    {
        // Arrange
        var (page, pane) = await OpenPaneAsync("Demo/MetadataValues");
        await Assertions.Expect(pane.GetByTestId("subject-pane-empty-values")).ToContainTextAsync("Fraction", new() { Timeout = PageLoadTimeout });
        await Assertions.Expect(pane.GetByTestId("metadata-summary")).ToHaveCountAsync(0);
        var storage = (FluentStorageContainer)_fixture.ServerServices.GetRequiredService<RootManager>().Root!;

        // Act - the method that agents call.
        await storage.SetMetadataAsync("Demo/MetadataValues", null, JsonElement.Parse("""{ "tags": ["e2e-agent"] }"""), CancellationToken.None);
        await storage.SetMetadataAsync("Demo/MetadataValues", "Fraction", JsonElement.Parse("""{ "notes": "Set by an agent" }"""), CancellationToken.None);

        // Assert - without a reload.
        await Assertions.Expect(pane.GetByTestId("metadata-summary").GetByTestId("metadata-tag")).ToHaveTextAsync("e2e-agent", new() { Timeout = PageLoadTimeout });
        var row = GetRow(page, pane, "Fraction");
        await Assertions.Expect(row.GetByTestId("subject-pane-value-metadata")).ToHaveCountAsync(1, new() { Timeout = PageLoadTimeout });
        await Assertions.Expect(pane.GetByTestId("subject-pane-empty-values")).Not.ToContainTextAsync("Fraction");
    });
}
