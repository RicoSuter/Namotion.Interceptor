using HomeBlaze.E2E.Tests.Infrastructure;
using HomeBlaze.E2E.Tests.Models;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// Every shipped widget shows a value that changes on the server while the page is open, without a reload. Each
/// component records the values its own render reads, so a widget that reads a value anywhere else stops updating.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class LiveUpdateTests
{
    private const int Timeout = 30000;

    // The page that these tests overwrite.
    private const string ScratchPage = "LiveUpdates.md";

    // Replaced by the absolute path of the subject that a test changes.
    private const string PathPlaceholder = "$P";

    private readonly PlaywrightFixture _fixture;

    public LiveUpdateTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    public static TheoryData<string, string> TextWidgets => new()
    {
        { "ValueTile", ":: HomeBlaze.Components.ValueTile Path=$P/Text Label=\"Live\"\n::\n" },
        { "ValueList", ":: HomeBlaze.Components.ValueList\n::: Value Path=$P/Text Label=\"Live\"\n::\n" },
        { "ValueList content", ":: HomeBlaze.Components.ValueList\n::: Value Label=\"Live\"\nShows {{ $P/Text }}\n::\n" },
        { "Indicator", ":: HomeBlaze.Components.Indicator Path=$P/Text\n::\n" },
        { "Control", ":: HomeBlaze.Components.Control Path=$P/Text Label=\"Live\"\n::\n" },
        { "Expression", "Start {{ $P/Text }} End\n" },
        { "Expression in a table", "| Value |\n|---|\n| {{ $P/Text }} |\n" },
        { "Alert", ":: HomeBlaze.Components.Alert Severity=Info\nAlert {{ $P/Text }}\n::\n" },
        { "Card", ":: HomeBlaze.Components.Card Title=\"Live Card\"\n::: HomeBlaze.Components.ValueTile Path=$P/Text Label=\"Live\"\n::\n" },
        { "Card text", ":: HomeBlaze.Components.Card Title=\"Live Card\"\nCard {{ $P/Text }}\n::\n" },
        { "Responsive", ":: HomeBlaze.Components.Responsive Show=NotPhone\n::: HomeBlaze.Components.ValueTile Path=$P/Text Label=\"Live\"\n::\n" },
        { "FlowLayout", ":: HomeBlaze.Components.FlowLayout MinWidth=200\n::: Item\n:::: HomeBlaze.Components.ValueTile Path=$P/Text Label=\"Live\"\n::\n" },
        { "GridLayout", ":: HomeBlaze.Components.GridLayout Rows=1 Columns=1\n::: Cell Row=0 Column=0\n:::: HomeBlaze.Components.ValueTile Path=$P/Text Label=\"Live\"\n::\n" },
        { "CanvasLayout", ":: HomeBlaze.Components.CanvasLayout\n::: Node X=20 Y=20 Width=300 Height=150\n:::: HomeBlaze.Components.ValueTile Path=$P/Text Label=\"Live\"\n::\n" },
        { "TabLayout", ":: HomeBlaze.Components.TabLayout\n::: Tab Title=\"Shown\"\n:::: HomeBlaze.Components.ValueTile Path=$P/Text Label=\"Live\"\n::\n" },
        { "ExpansionPanels", ":: HomeBlaze.Components.ExpansionPanels\n::: Panel Title=\"Open\" Expanded=true\n:::: HomeBlaze.Components.ValueTile Path=$P/Text Label=\"Live\"\n::\n" },
        { "Widget reference", ":: HomeBlaze.Components.ValueTile#livetile Path=$P/Text Label=\"Live\"\n::\n\n:: HomeBlaze.Components.Widget Path=livetile\n::\n" },
        { "SubjectTable", ":: HomeBlaze.Components.SubjectTable Source=\"/\" Type=HomeBlaze.E2E.Tests.Models.LiveValuesSubject\n::: Column Property=Text Label=\"Text\"\n::\n" },
        { "HistoryChart legend", ":: HomeBlaze.History.Blazor.HistoryChart Range=1h Height=200\n::: Series Path=$P/Text Label=\"Live\"\n::\n" },
    };

    [Theory]
    [MemberData(nameof(TextWidgets))]
    public async Task WhenAValueChangesOnTheServer_ThenTheWidgetShowsTheNewValue(string widget, string content)
    {
        // Arrange
        var (subject, page) = await OpenPageAsync(content, live => live.Text = $"Before {widget}");
        var markdown = page.Locator(".markdown-content");
        await Assertions.Expect(markdown).ToContainTextAsync($"Before {widget}", new() { Timeout = Timeout });

        // Act & Assert
        subject.Text = $"After {widget}";
        await Assertions.Expect(markdown).ToContainTextAsync($"After {widget}", new() { Timeout = Timeout });

        subject.Text = $"Again {widget}";
        await Assertions.Expect(markdown).ToContainTextAsync($"Again {widget}", new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenAValueInAHiddenTabChanges_ThenTheTabShowsTheNewValueWhenOpenedAndAfterwards()
    {
        // Arrange
        var content = ":: HomeBlaze.Components.TabLayout\n::: Tab Title=\"First\"\nNothing here.\n" +
            "::: Tab Title=\"Second\"\n:::: HomeBlaze.Components.ValueTile Path=$P/Text Label=\"Live\"\n::\n";
        var (subject, page) = await OpenPageAsync(content, live => live.Text = "Before");
        var markdown = page.Locator(".markdown-content");

        // Act
        subject.Text = "While hidden";
        await page.GetByRole(AriaRole.Tab, new() { Name = "Second" }).ClickAsync();

        // Assert
        await Assertions.Expect(markdown).ToContainTextAsync("While hidden", new() { Timeout = Timeout });
        subject.Text = "While shown";
        await Assertions.Expect(markdown).ToContainTextAsync("While shown", new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenAValueInAClosedPanelChanges_ThenThePanelShowsTheNewValueWhenOpenedAndAfterwards()
    {
        // Arrange
        var content = ":: HomeBlaze.Components.ExpansionPanels\n::: Panel Title=\"Closed\" Expanded=false\n" +
            ":::: HomeBlaze.Components.ValueTile Path=$P/Text Label=\"Live\"\n::\n";
        var (subject, page) = await OpenPageAsync(content, live => live.Text = "Before");
        var markdown = page.Locator(".markdown-content");

        // Act
        subject.Text = "While closed";
        await page.GetByTestId("expansion-panel").Filter(new() { HasText = "Closed" }).GetByText("Closed", new() { Exact = true }).ClickAsync();

        // Assert
        await Assertions.Expect(markdown).ToContainTextAsync("While closed", new() { Timeout = Timeout });
        subject.Text = "While open";
        await Assertions.Expect(markdown).ToContainTextAsync("While open", new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenANumberChanges_ThenTheGaugeShowsIt()
    {
        // Arrange
        var content = ":: HomeBlaze.Components.Gauge Path=$P/Level Minimum=0 Maximum=100 Label=\"Live\"\n::\n";
        var (subject, page) = await OpenPageAsync(content, live => live.Level = 37);
        var markdown = page.Locator(".markdown-content");
        await Assertions.Expect(markdown).ToContainTextAsync("37", new() { Timeout = Timeout });

        // Act
        subject.Level = 64;

        // Assert
        await Assertions.Expect(markdown).ToContainTextAsync("64", new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenANumberChanges_ThenTheProgressBarShowsIt()
    {
        // Arrange
        var content = ":: HomeBlaze.Components.Progress Path=$P/Level Minimum=0 Maximum=100\n::\n";
        var (subject, page) = await OpenPageAsync(content, live => live.Level = 20);
        var bar = page.Locator(".markdown-content [role=progressbar]").First;
        await Assertions.Expect(bar).ToHaveAttributeAsync("aria-valuenow", "20", new() { Timeout = Timeout });

        // Act
        subject.Level = 80;

        // Assert
        await Assertions.Expect(bar).ToHaveAttributeAsync("aria-valuenow", "80", new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenANumberChanges_ThenTheSliderShowsIt()
    {
        // Arrange
        var content = ":: HomeBlaze.Components.Slider Path=$P/Level Minimum=0 Maximum=100\n::\n";
        var (subject, page) = await OpenPageAsync(content, live => live.Level = 20);
        var slider = page.Locator(".markdown-content input[type=range]").First;
        await Assertions.Expect(slider).ToHaveValueAsync("20", new() { Timeout = Timeout });

        // Act
        subject.Level = 80;

        // Assert
        await Assertions.Expect(slider).ToHaveValueAsync("80", new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenABooleanChanges_ThenTheSwitchShowsIt()
    {
        // Arrange
        var content = ":: HomeBlaze.Components.Switch Path=$P/IsOn Label=\"Live\"\n::\n";
        var (subject, page) = await OpenPageAsync(content, live => live.IsOn = false);
        var toggle = page.Locator(".markdown-content input[type=checkbox]").First;
        await Assertions.Expect(toggle).Not.ToBeCheckedAsync(new() { Timeout = Timeout });

        // Act
        subject.IsOn = true;

        // Assert
        await Assertions.Expect(toggle).ToBeCheckedAsync(new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenTheEnabledStateOfAnOperationChanges_ThenTheButtonShowsIt()
    {
        // Arrange
        var content = ":: HomeBlaze.Components.OperationButton Path=$P/Run\n::\n";
        var (subject, page) = await OpenPageAsync(content, live => live.IsOn = false);
        var button = page.Locator(".markdown-content").GetByTestId("operation-button");
        await Assertions.Expect(button).ToBeDisabledAsync(new() { Timeout = Timeout });

        // Act
        subject.IsOn = true;

        // Assert
        await Assertions.Expect(button).ToBeEnabledAsync(new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenAValueInACellOfAVirtualizedDataGridChanges_ThenTheCellShowsIt()
    {
        // Arrange
        var (rows, page) = await OpenGridPageAsync();
        var grid = page.GetByTestId("live-grid");
        await Assertions.Expect(grid.GetByTestId("grid-cell").GetByText("Row 0", new() { Exact = true })).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Act
        rows[0].Text = "Row 0 changed";

        // Assert
        await Assertions.Expect(grid.GetByText("Row 0 changed", new() { Exact = true })).ToBeVisibleAsync(new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenARowThatScrollingRenderedChanges_ThenTheVirtualizedDataGridShowsIt()
    {
        // Arrange
        var (rows, page) = await OpenGridPageAsync();
        var grid = page.GetByTestId("live-grid");
        await Assertions.Expect(grid.GetByText("Row 0", new() { Exact = true })).ToBeVisibleAsync(new() { Timeout = Timeout });
        await Assertions.Expect(grid.GetByText("Row 199", new() { Exact = true })).ToHaveCountAsync(0);
        await grid.Locator(".mud-table-container").EvaluateAsync("element => element.scrollTop = element.scrollHeight");
        await Assertions.Expect(grid.GetByText("Row 199", new() { Exact = true })).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Act
        rows[199].Text = "Row 199 changed";

        // Assert
        await Assertions.Expect(grid.GetByText("Row 199 changed", new() { Exact = true })).ToBeVisibleAsync(new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenAValueInARowTemplateOfATableChanges_ThenTheRowShowsIt()
    {
        // Arrange
        var (rows, page) = await OpenGridPageAsync();
        var table = page.GetByTestId("live-table");
        await Assertions.Expect(table.GetByText("Row 1", new() { Exact = true })).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Act
        rows[1].Text = "Row 1 changed";

        // Assert
        await Assertions.Expect(table.GetByText("Row 1 changed", new() { Exact = true })).ToBeVisibleAsync(new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenAValueInAnOpenMenuChanges_ThenThePopoverShowsIt()
    {
        // Arrange
        var (rows, page) = await OpenGridPageAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Live menu" }).ClickAsync();
        var item = page.GetByRole(AriaRole.Menuitem).Filter(new() { HasText = "Row 0" });
        await Assertions.Expect(item).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Act
        rows[0].Text = "Row 0 in the menu";

        // Assert
        await Assertions.Expect(page.GetByRole(AriaRole.Menuitem).Filter(new() { HasText = "Row 0 in the menu" })).ToBeVisibleAsync(new() { Timeout = Timeout });
    }

    private async Task<(LiveValuesSubject[] Rows, IPage Page)> OpenGridPageAsync()
    {
        var (name, grid) = await _fixture.AddSubjectToRootAsync<LiveGridSubject>();
        var rows = Enumerable.Range(0, 200).Select(index => new LiveValuesSubject { Text = $"Row {index}" }).ToArray();
        grid.Rows = rows;
        await _fixture.WriteDemoPageAsync(ScratchPage,
            $"---\ntitle: Live Updates\n---\n\n# Live Updates\n\n:: HomeBlaze.Components.Widget Path=/{name}\n::\n");

        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/{ScratchPage}");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        await page.WaitUntilInteractiveAsync();
        return (rows, page);
    }

    private async Task<(LiveValuesSubject Subject, IPage Page)> OpenPageAsync(string content, Action<LiveValuesSubject> initialize)
    {
        var (name, subject) = await _fixture.AddSubjectToRootAsync<LiveValuesSubject>();
        initialize(subject);
        await _fixture.WriteDemoPageAsync(ScratchPage,
            "---\ntitle: Live Updates\n---\n\n# Live Updates\n\n" + content.Replace(PathPlaceholder, "/" + name, StringComparison.Ordinal));

        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/{ScratchPage}");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // A change before the circuit is connected would be picked up by its first render, which proves nothing.
        await page.WaitUntilInteractiveAsync();
        return (subject, page);
    }
}
