using System.Text.RegularExpressions;
using HomeBlaze.Components;
using HomeBlaze.E2E.Tests.Infrastructure;
using HomeBlaze.E2E.Tests.Models;
using HomeBlaze.Samples;
using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Testing;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for SubjectSetupDialog: the type picker, the configure page and the create action.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class SubjectSetupDialogTests
{
    private const int PageLoadTimeout = 30000;
    private const int ElementVisibilityTimeout = 5000;
    private const string MotorName = "Test motor";

    private readonly PlaywrightFixture _fixture;

    public SubjectSetupDialogTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// The Create operation of the subject pane the dialog is opened from. Scoped to the pane
    /// container so it cannot resolve to the dialog's own Create button.
    /// </summary>
    private static ILocator PaneCreateButton(IPage page)
    {
        return page.Locator("#scrollContainer button:has-text('Create')").First;
    }

    private static ILocator Dialog(IPage page) => page.Locator("[data-testid='create-subject-dialog']");

    private static ILocator DialogTitle(IPage page) => page.Locator("[data-testid='create-dialog-title']");

    private static ILocator SearchInput(IPage page) => page.GetByPlaceholder("Search types");

    private static ILocator TypeRows(IPage page) => page.Locator("[data-testid^='type-row-']");

    private static ILocator TypeRow(IPage page, string typeName) => page.Locator($"[data-testid='type-row-{typeName}']");

    private static ILocator NameInput(IPage page) => page.GetByLabel("File name");

    private static ILocator CreateButton(IPage page) => page.Locator("[data-testid='create-button']");

    private static ILocator BackButton(IPage page) => page.Locator("[data-testid='back-button']");

    private static ILocator NameHelperText(IPage page, string text)
    {
        return Dialog(page).Locator(".mud-input-helper-text").Filter(new() { HasText = text });
    }

    private async Task OpenSubjectBrowserAsync(IPage page)
    {
        await page.GotoAsync($"{_fixture.ServerAddress}");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // The automatic timezone resolves through JS after interactive rendering. Prerendered
        // links are already clickable, but navigating then can race the old page's hydration.
        await Assertions.Expect(page.GetByTitle(new Regex(@"^Automatic \(")))
            .ToBeVisibleAsync(new() { Timeout = PageLoadTimeout });

        var browserLink = page.GetByRole(AriaRole.Link, new() { Name = "Browser" });
        await Assertions.Expect(browserLink).ToBeVisibleAsync(new() { Timeout = PageLoadTimeout });
        await browserLink.ClickAsync();
        await page.WaitForURLAsync(url => url.Contains("/browser"), new() { Timeout = PageLoadTimeout });

        // The URL changes before the browser has built its panes, and the pane's Create operation is
        // what the dialog is opened from, so that button is the condition the next step depends on.
        await Assertions.Expect(PaneCreateButton(page)).ToBeVisibleAsync(new() { Timeout = PageLoadTimeout });
    }

    private async Task OpenDialogAsync(IPage page)
    {
        await OpenSubjectBrowserAsync(page);
        await PaneCreateButton(page).ClickAsync();

        // The dialog frame appears before its content, so the first type row is the condition to hold for.
        await Assertions.Expect(TypeRows(page).First).ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });
    }

    private static async Task PickTypeAsync(IPage page, string typeName)
    {
        await TypeRow(page, typeName).ClickAsync();
        await Assertions.Expect(CreateButton(page)).ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });
    }

    /// <summary>
    /// Enters the motor's own name, which the motor form requires and which starts empty, so Create
    /// stays disabled for a motor until this has run.
    /// </summary>
    private static async Task EnterMotorNameAsync(IPage page)
    {
        await Dialog(page).GetByLabel("Name", new() { Exact = true }).FillAsync(MotorName);
    }

    private static bool FileContains(string filePath, string text)
    {
        try
        {
            return File.ReadAllText(filePath).Contains(text, StringComparison.Ordinal);
        }
        catch (IOException)
        {
            // The writer can still hold the file, or has not created it yet.
            return false;
        }
    }

    [Fact]
    public void WhenTypesAreOfferedForCreation_ThenEveryIconNameResolves()
    {
        // Arrange
        var catalog = _fixture.ServerServices.GetRequiredService<SubjectTypeCatalog>();
        var fallbackIcon = MudBlazorIconResolver.Resolve(null);

        // Act
        var descriptors = catalog.GetCreatableTypes([]);
        var unresolvedIcons = descriptors
            .Where(descriptor => descriptor.IconName != "Article" &&
                                 MudBlazorIconResolver.Resolve(descriptor.IconName) == fallbackIcon)
            .Select(descriptor => $"{descriptor.Type.Name}: {descriptor.IconName}")
            .ToList();

        // Assert - an unknown name silently shows the fallback icon instead of failing.
        Assert.NotEmpty(descriptors);
        Assert.Empty(unresolvedIcons);
    }

    [Fact]
    public async Task WhenTheDialogOpens_ThenThePickerIsShownWithoutANameField()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();

        // Act
        await OpenDialogAsync(page);

        // Assert
        await Assertions.Expect(DialogTitle(page)).ToHaveTextAsync("Create subject");
        await Assertions.Expect(SearchInput(page)).ToBeFocusedAsync(new() { Timeout = ElementVisibilityTimeout });
        await Assertions.Expect(page.Locator("[data-testid='type-group-all']")).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator("[data-testid='create-name-input']")).ToHaveCountAsync(0);
        await Assertions.Expect(CreateButton(page)).ToHaveCountAsync(0);
        await Assertions.Expect(BackButton(page)).ToHaveCountAsync(0);
        await Assertions.Expect(page.Locator("[data-testid='cancel-button']")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task WhenTheDialogOpens_ThenThePickerIsDescribedForAssistiveTechnology()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        var allGroup = page.Locator("[data-testid='type-group-all']");
        var dashboardGroup = page.Locator("[data-testid='type-group-dashboard']");

        // Act
        await OpenDialogAsync(page);

        // Assert - the placeholder alone would also give the input a name, so the attribute itself is read.
        await Assertions.Expect(SearchInput(page)).ToHaveAttributeAsync("aria-label", "Search types");
        await Assertions.Expect(page.GetByRole(AriaRole.Group, new() { Name = "Type groups" })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Dashboard", Exact = true, Level = 3 }))
            .ToBeVisibleAsync();

        // The visible text stays "Dashboard 3", while the name that is read out says what the number counts.
        var dashboardCount = int.Parse(await dashboardGroup.Locator("span").Last.InnerTextAsync());
        Assert.True(dashboardCount > 1);
        await Assertions.Expect(dashboardGroup).ToHaveTextAsync(new Regex($@"^\s*Dashboard\s*{dashboardCount}\s*$"));
        await Assertions.Expect(dashboardGroup).ToHaveAttributeAsync("aria-label", $"Dashboard, {dashboardCount} types");
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = $"Dashboard, {dashboardCount} types", Exact = true }))
            .ToBeVisibleAsync();

        // Act
        await SearchInput(page).FillAsync("simulated motor");

        // Assert
        await Assertions.Expect(allGroup).ToHaveAttributeAsync("aria-label", "All, 1 type", new() { Timeout = ElementVisibilityTimeout });
        await Assertions.Expect(dashboardGroup).ToHaveAttributeAsync("aria-label", "Dashboard, 0 types");

        // Act
        await SearchInput(page).FillAsync("no such type anywhere");

        // Assert
        await Assertions.Expect(page.Locator("[data-testid='type-empty-state']"))
            .ToHaveAttributeAsync("role", "status", new() { Timeout = ElementVisibilityTimeout });
    }

    [Fact]
    public async Task WhenATypeRowIsReachedWithTheKeyboard_ThenItShowsAFocusOutline()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await OpenDialogAsync(page);
        await Assertions.Expect(SearchInput(page)).ToBeFocusedAsync(new() { Timeout = ElementVisibilityTimeout });

        // Act - the group buttons come before the rows, and how many there are depends on the loaded types.
        var isTypeRowFocused = false;
        for (var tabCount = 0; tabCount < 40 && !isTypeRowFocused; tabCount++)
        {
            await page.Keyboard.PressAsync("Tab");
            isTypeRowFocused = await page.EvaluateAsync<bool>(
                "() => document.activeElement?.matches(\"[data-testid^='type-row-']\") === true");
        }

        // Assert
        Assert.True(isTypeRowFocused, "No type row was reached with the Tab key.");
        var outline = await page.EvaluateAsync<string[]>(
            "() => { const style = getComputedStyle(document.activeElement); return [style.outlineStyle, style.outlineWidth]; }");
        Assert.Equal(["solid", "2px"], outline);
    }

    [Theory]
    [InlineData(1300, 880)]
    [InlineData(375, 812)]
    [InlineData(812, 375)]
    public async Task WhenTheViewportIsDesktopOrPhoneSized_ThenTheTypeListAndTheActionsStayInView(int width, int height)
    {
        // Arrange - the navigation to the browser is in a drawer that a narrow viewport closes, so the
        // viewport is only resized once the pane is there.
        var page = await _fixture.CreatePageAsync();
        await OpenSubjectBrowserAsync(page);
        await page.SetViewportSizeAsync(width, height);

        // Act
        await PaneCreateButton(page).ClickAsync();
        await Assertions.Expect(TypeRows(page).First).ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });

        // Assert - everything is measured in one frame, because the dialog scales while it opens.
        var pickerLayout = await page.EvaluateAsync<double[]>(
            """
            () => {
                const isInViewport = box => box.top >= 0 && box.left >= 0 && box.bottom <= innerHeight && box.right <= innerWidth;
                const list = document.querySelector('.subject-type-list').getBoundingClientRect();
                const visibleRows = [...document.querySelectorAll("[data-testid^='type-row-']")]
                    .map(row => row.getBoundingClientRect())
                    .filter(row => row.height > 0 && row.top >= list.top - 0.5 && row.bottom <= list.bottom + 0.5 && isInViewport(row));
                const content = document.querySelector('.mud-dialog-content');
                const cancel = document.querySelector("[data-testid='cancel-button']").getBoundingClientRect();
                return [visibleRows.length, content.scrollHeight - content.clientHeight, isInViewport(cancel) ? 1 : 0];
            }
            """);

        Assert.True(pickerLayout[0] >= 3, $"Only {pickerLayout[0]} type rows are completely visible in the list.");
        Assert.True(pickerLayout[1] <= 0, $"The dialog content scrolls by {pickerLayout[1]} pixels, although only the list should.");
        Assert.True(pickerLayout[2] == 1, "The cancel button is outside the viewport.");

        // Act
        await PickTypeAsync(page, "shellydevice");

        // Assert - left, top, right and bottom of "Back", "Cancel" and "Create", which share one line.
        await Assertions.Expect(BackButton(page)).ToHaveTextAsync("Back");
        var actionsLayout = await page.EvaluateAsync<double[]>(
            """
            () => ['back-button', 'cancel-button', 'create-button'].flatMap(testId => {
                const box = document.querySelector(`[data-testid='${testId}']`).getBoundingClientRect();
                const isInViewport = box.top >= 0 && box.left >= 0 && box.bottom <= innerHeight && box.right <= innerWidth;
                return [box.left, box.top, box.right, isInViewport ? 1 : 0];
            })
            """);

        Assert.Equal([1, 1, 1], new[] { actionsLayout[3], actionsLayout[7], actionsLayout[11] });
        Assert.Equal(actionsLayout[1], actionsLayout[5], 0.5);
        Assert.Equal(actionsLayout[1], actionsLayout[9], 0.5);
        Assert.True(actionsLayout[2] <= actionsLayout[4], "\"Back\" overlaps \"Cancel\".");
        Assert.True(actionsLayout[6] <= actionsLayout[8], "\"Cancel\" overlaps \"Create\".");
    }

    [Fact]
    public async Task WhenTheDeviceIsATouchDevice_ThenNoFieldTakesTheFocus()
    {
        // Arrange - a field that takes the focus opens the on-screen keyboard over the list or the form.
        var page = await _fixture.CreatePageAsync(new BrowserNewContextOptions { HasTouch = true, IsMobile = true });
        await OpenSubjectBrowserAsync(page);
        await page.SetViewportSizeAsync(375, 812);
        await page.EvaluateAsync(
            """
            () => {
                window.focusedTestIds = [];
                document.addEventListener('focusin', event => window.focusedTestIds.push(event.target.getAttribute('data-testid') ?? event.target.tagName));
            }
            """);

        var allGroup = page.Locator("[data-testid='type-group-all']");
        var dashboardGroup = page.Locator("[data-testid='type-group-dashboard']");

        // Act - each focus move follows its page from the server, so a later round trip, which the tap on
        // a group is, has it behind it.
        await PaneCreateButton(page).TapAsync();
        await Assertions.Expect(TypeRows(page).First).ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });
        await dashboardGroup.TapAsync();
        await Assertions.Expect(dashboardGroup).ToHaveAttributeAsync("aria-pressed", "true", new() { Timeout = ElementVisibilityTimeout });

        // Assert
        await Assertions.Expect(TypeRow(page, "gridlayout")).ToBeVisibleAsync();
        await Assertions.Expect(SearchInput(page)).Not.ToBeFocusedAsync();
        var focusedAfterOpening = await page.EvaluateAsync<string[]>("() => window.focusedTestIds");
        Assert.Contains("type-group-dashboard", focusedAfterOpening);
        Assert.DoesNotContain("type-search-input", focusedAfterOpening);

        // Act - picking a type shows the name field, and "Back" shows the search field again.
        await TypeRow(page, "gridlayout").TapAsync();
        await Assertions.Expect(page.Locator("[data-testid='create-name-input']"))
            .ToHaveValueAsync("GridLayout", new() { Timeout = ElementVisibilityTimeout });
        await BackButton(page).TapAsync();
        await Assertions.Expect(DialogTitle(page)).ToHaveTextAsync("Create subject", new() { Timeout = ElementVisibilityTimeout });
        await allGroup.TapAsync();
        await Assertions.Expect(allGroup).ToHaveAttributeAsync("aria-pressed", "true", new() { Timeout = ElementVisibilityTimeout });

        // Assert
        var focusedAfterGoingBack = await page.EvaluateAsync<string[]>("() => window.focusedTestIds");
        Assert.Contains("type-group-all", focusedAfterGoingBack);
        Assert.DoesNotContain("create-name-input", focusedAfterGoingBack);
        Assert.DoesNotContain("type-search-input", focusedAfterGoingBack);
    }

    [Fact]
    public async Task WhenTheDialogOpens_ThenTheTypeRowsAreLeftAligned()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();

        // Act
        await OpenDialogAsync(page);

        // Assert - rows are equally wide, so centred content would start further left the longer its text is.
        await Assertions.Expect(TypeRow(page, "gpiosubject")).ToBeVisibleAsync();
        await Assertions.Expect(TypeRow(page, "luxtronikheatpump")).ToBeVisibleAsync();

        // The dialog scales while it opens, so both icons are measured in the same frame.
        var iconOffsets = await page.EvaluateAsync<double[]>(
            "testIds => testIds.map(testId => document.querySelector(`[data-testid='${testId}'] .mud-icon-root`).getBoundingClientRect().left)",
            new[] { "type-row-gpiosubject", "type-row-luxtronikheatpump" });

        Assert.Equal(iconOffsets[0], iconOffsets[1], 0.5);
    }

    [Fact]
    public async Task WhenTheDialogOpens_ThenTypesThatMustNotBeCreatedAreNotListed()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();

        // Act
        await OpenDialogAsync(page);

        // Assert - the cell and node types only exist inside a layout, and the test root already has a plugin manager.
        await Assertions.Expect(TypeRow(page, "gridlayout")).ToBeVisibleAsync();
        await Assertions.Expect(TypeRow(page, "gridcell")).ToHaveCountAsync(0);
        await Assertions.Expect(TypeRow(page, "canvasnode")).ToHaveCountAsync(0);
        await Assertions.Expect(TypeRow(page, "pluginmanager")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task WhenSearching_ThenTheListAndTheGroupCountsAreNarrowed()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await OpenDialogAsync(page);

        // Act
        await SearchInput(page).FillAsync("simulated motor");

        // Assert
        await Assertions.Expect(TypeRows(page)).ToHaveCountAsync(1, new() { Timeout = ElementVisibilityTimeout });
        await Assertions.Expect(TypeRow(page, "motor")).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator("[data-testid='type-group-all']")).ToHaveTextAsync(new Regex(@"^\s*All\s*1\s*$"));
        await Assertions.Expect(page.Locator("[data-testid='type-group-dashboard']")).ToHaveTextAsync(new Regex(@"^\s*Dashboard\s*0\s*$"));
    }

    [Fact]
    public async Task WhenNothingMatchesTheSearch_ThenTheEmptyStateOffersToClearIt()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await OpenDialogAsync(page);

        // Act
        await SearchInput(page).FillAsync("no such type anywhere");

        // Assert
        await Assertions.Expect(page.Locator("[data-testid='type-empty-state']"))
            .ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });
        await Assertions.Expect(TypeRows(page)).ToHaveCountAsync(0);

        // Act
        await page.Locator("[data-testid='type-clear-search-button']").ClickAsync();

        // Assert
        await Assertions.Expect(TypeRow(page, "motor")).ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });
        await Assertions.Expect(SearchInput(page)).ToHaveValueAsync("");
    }

    [Fact]
    public async Task WhenAGroupIsSelected_ThenOnlyThatGroupIsListed()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await OpenDialogAsync(page);

        var dashboardGroup = page.Locator("[data-testid='type-group-dashboard']");

        // Act
        await dashboardGroup.ClickAsync();

        // Assert
        await Assertions.Expect(TypeRow(page, "motor")).ToHaveCountAsync(0, new() { Timeout = ElementVisibilityTimeout });
        await Assertions.Expect(TypeRow(page, "gridlayout")).ToBeVisibleAsync();
        await Assertions.Expect(TypeRow(page, "canvaslayout")).ToBeVisibleAsync();
        await Assertions.Expect(TypeRow(page, "widget")).ToBeVisibleAsync();

        var groupCount = int.Parse(await dashboardGroup.Locator("span").Last.InnerTextAsync());
        Assert.True(groupCount > 0);
        await Assertions.Expect(TypeRows(page)).ToHaveCountAsync(groupCount);
    }

    [Fact]
    public async Task WhenSearchingWhileAGroupIsSelected_ThenAllGroupsAreSearched()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await OpenDialogAsync(page);
        var allGroup = page.Locator("[data-testid='type-group-all']");
        var dashboardGroup = page.Locator("[data-testid='type-group-dashboard']");
        await dashboardGroup.ClickAsync();
        await Assertions.Expect(dashboardGroup).ToHaveAttributeAsync("aria-pressed", "true");

        // Act
        await SearchInput(page).FillAsync("simulated motor");

        // Assert
        await Assertions.Expect(TypeRow(page, "motor")).ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });
        await Assertions.Expect(allGroup).ToHaveAttributeAsync("aria-pressed", "true");
        await Assertions.Expect(dashboardGroup).ToHaveAttributeAsync("aria-pressed", "false");
    }

    [Fact]
    public async Task WhenAGroupIsSelectedDuringASearch_ThenItNarrowsTheMatchesAndSurvivesClearingTheSearch()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await OpenDialogAsync(page);
        var dashboardGroup = page.Locator("[data-testid='type-group-dashboard']");
        await SearchInput(page).FillAsync("simulated motor");
        await Assertions.Expect(TypeRow(page, "motor")).ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });

        // Act
        await dashboardGroup.ClickAsync();

        // Assert
        await Assertions.Expect(page.Locator("[data-testid='type-empty-state']"))
            .ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });
        await Assertions.Expect(TypeRows(page)).ToHaveCountAsync(0);

        // Act
        await page.Locator("[data-testid='type-clear-search-button']").ClickAsync();

        // Assert
        await Assertions.Expect(TypeRow(page, "gridlayout")).ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });
        await Assertions.Expect(TypeRow(page, "motor")).ToHaveCountAsync(0);
        await Assertions.Expect(dashboardGroup).ToHaveAttributeAsync("aria-pressed", "true");
    }

    [Fact]
    public async Task WhenATypeIsPicked_ThenTheConfigurePageShowsTheSuggestedName()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await OpenDialogAsync(page);

        // Act
        await PickTypeAsync(page, "motor");

        // Assert
        await Assertions.Expect(DialogTitle(page)).ToHaveTextAsync("Create Motor");
        await Assertions.Expect(NameInput(page)).ToHaveValueAsync("Motor");
        await Assertions.Expect(NameInput(page)).ToBeFocusedAsync(new() { Timeout = ElementVisibilityTimeout });
        await Assertions.Expect(Dialog(page).GetByText(".json", new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(Dialog(page).GetByText("Part of the subject's path, which is its ID.")).ToBeVisibleAsync();

        // Other tests take the absence of this test id as "the dialog asks for no name".
        await Assertions.Expect(page.Locator("[data-testid='create-name-input']")).ToHaveValueAsync("Motor");

        // The suggested name is valid as it is, so the motor's own required name is all that Create waits for.
        // The dialog disables Create itself until a form reports, so the form must be there before this is read.
        var motorNameInput = Dialog(page).GetByLabel("Name", new() { Exact = true });
        await Assertions.Expect(motorNameInput).ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });
        await Assertions.Expect(CreateButton(page)).ToBeDisabledAsync(new() { Timeout = ElementVisibilityTimeout });

        // Act
        await EnterMotorNameAsync(page);

        // Assert
        await Assertions.Expect(CreateButton(page)).ToBeEnabledAsync(new() { Timeout = ElementVisibilityTimeout });

        // Act
        await motorNameInput.FillAsync("");

        // Assert
        await Assertions.Expect(CreateButton(page)).ToBeDisabledAsync(new() { Timeout = ElementVisibilityTimeout });
    }

    [Fact]
    public async Task WhenGoingBack_ThenTheSearchIsPreservedAndAnotherTypeGetsItsOwnForm()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await OpenDialogAsync(page);
        await SearchInput(page).FillAsync("history");
        await PickTypeAsync(page, "inmemoryhistorystoresubject");
        await Assertions.Expect(DialogTitle(page)).ToHaveTextAsync("Create In-memory history");

        // Only the in-memory form has this field, and only the SQLite form has a database path.
        var inMemoryOnlyField = Dialog(page).GetByLabel("Max Points Per Property");
        var sqliteOnlyField = Dialog(page).GetByLabel("Database Path");
        await Assertions.Expect(inMemoryOnlyField).ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });
        await Assertions.Expect(sqliteOnlyField).ToHaveCountAsync(0);

        // Act
        await BackButton(page).ClickAsync();

        // Assert
        await Assertions.Expect(DialogTitle(page)).ToHaveTextAsync("Create subject");
        await Assertions.Expect(SearchInput(page)).ToHaveValueAsync("history");
        await Assertions.Expect(SearchInput(page)).ToBeFocusedAsync(new() { Timeout = ElementVisibilityTimeout });
        await Assertions.Expect(CreateButton(page)).ToHaveCountAsync(0);
        await Assertions.Expect(BackButton(page)).ToHaveCountAsync(0);

        // Act
        await PickTypeAsync(page, "sqlitehistorystoresubject");

        // Assert
        await Assertions.Expect(DialogTitle(page)).ToHaveTextAsync("Create SQLite history");
        await Assertions.Expect(NameInput(page)).ToHaveValueAsync("SqliteHistoryStoreSubject");
        await Assertions.Expect(sqliteOnlyField).ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });
        await Assertions.Expect(inMemoryOnlyField).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task WhenAnotherTypeIsPickedAfterGoingBack_ThenAnEditedNameIsKeptAndAClearedNameIsSuggestedAgain()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await OpenDialogAsync(page);
        await PickTypeAsync(page, "motor");
        await Assertions.Expect(NameInput(page)).ToHaveValueAsync("Motor");

        // Act
        await NameInput(page).FillAsync("Pump");
        await BackButton(page).ClickAsync();
        await PickTypeAsync(page, "shellydevice");

        // Assert
        await Assertions.Expect(DialogTitle(page)).ToHaveTextAsync("Create Shelly device");
        await Assertions.Expect(NameInput(page)).ToHaveValueAsync("Pump");

        // Act
        await NameInput(page).FillAsync("");
        await Assertions.Expect(NameHelperText(page, "File name is required"))
            .ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });
        await BackButton(page).ClickAsync();
        await PickTypeAsync(page, "motor");

        // Assert
        await Assertions.Expect(DialogTitle(page)).ToHaveTextAsync("Create Motor");
        await Assertions.Expect(NameInput(page)).ToHaveValueAsync("Motor");
        await Assertions.Expect(NameHelperText(page, "File name is required")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task WhenARequiredFieldIsEmpty_ThenCreateIsDisabled()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await OpenDialogAsync(page);

        var hostAddressInput = Dialog(page).GetByLabel("Host Address (IP or hostname)");

        // Act
        await PickTypeAsync(page, "shellydevice");

        // Assert - the dialog disables Create itself until a form reports, so the form must be there
        // before this is read.
        await Assertions.Expect(hostAddressInput).ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });
        await Assertions.Expect(CreateButton(page)).ToBeDisabledAsync(new() { Timeout = ElementVisibilityTimeout });

        // Act
        await hostAddressInput.FillAsync("192.0.2.1");

        // Assert
        await Assertions.Expect(CreateButton(page)).ToBeEnabledAsync(new() { Timeout = ElementVisibilityTimeout });

        // Act
        await hostAddressInput.FillAsync("");

        // Assert - only the form can have disabled it again.
        await Assertions.Expect(CreateButton(page)).ToBeDisabledAsync(new() { Timeout = ElementVisibilityTimeout });
    }

    [Fact]
    public async Task WhenTheGeneratedPropertiesEditorIsUntouched_ThenCreateFollowsItsPrefilledValues()
    {
        // Arrange - no type of the application is without an edit component, so one of this assembly is
        // registered. Resolving a qualified name adds the type to the registry for the rest of the host's life.
        var typeRegistry = _fixture.ServerServices.GetRequiredService<SubjectTypeRegistry>();
        Assert.NotNull(typeRegistry.ResolveType(typeof(PlainConfigurableSubject).AssemblyQualifiedName!));

        var page = await _fixture.CreatePageAsync();
        await OpenDialogAsync(page);
        var intervalInput = Dialog(page).GetByLabel("Interval");

        // Act
        await PickTypeAsync(page, "plainconfigurablesubject");

        // Assert - the required number is prefilled, so nothing has to be touched first.
        await Assertions.Expect(intervalInput).ToHaveValueAsync("5", new() { Timeout = ElementVisibilityTimeout });
        await Assertions.Expect(CreateButton(page)).ToBeEnabledAsync(new() { Timeout = ElementVisibilityTimeout });
        await Assertions.Expect(Dialog(page).GetByText("Required")).ToHaveCountAsync(0);

        // Act
        await intervalInput.FillAsync("");
        await intervalInput.BlurAsync();

        // Assert
        await Assertions.Expect(CreateButton(page)).ToBeDisabledAsync(new() { Timeout = ElementVisibilityTimeout });

        // Act
        await intervalInput.FillAsync("7");
        await intervalInput.BlurAsync();

        // Assert
        await Assertions.Expect(CreateButton(page)).ToBeEnabledAsync(new() { Timeout = ElementVisibilityTimeout });
    }

    [Fact]
    public async Task WhenTheNameIsInvalid_ThenCreateIsDisabledAndTheReasonIsShown()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await OpenDialogAsync(page);
        await PickTypeAsync(page, "motor");
        await EnterMotorNameAsync(page);

        // Create is enabled here, so only the name can disable it below.
        await Assertions.Expect(CreateButton(page)).ToBeEnabledAsync(new() { Timeout = ElementVisibilityTimeout });

        // Act & Assert - the test root already contains History.json
        await NameInput(page).FillAsync("history");
        await Assertions.Expect(NameHelperText(page, "This file name is already used"))
            .ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });
        await Assertions.Expect(CreateButton(page)).ToBeDisabledAsync();

        await NameInput(page).FillAsync("invalid/name");
        await Assertions.Expect(NameHelperText(page, "File name contains invalid characters"))
            .ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });
        await Assertions.Expect(CreateButton(page)).ToBeDisabledAsync();

        await NameInput(page).FillAsync("");
        await Assertions.Expect(NameHelperText(page, "File name is required"))
            .ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });
        await Assertions.Expect(CreateButton(page)).ToBeDisabledAsync();

        await NameInput(page).FillAsync("ValidName");
        await Assertions.Expect(CreateButton(page)).ToBeEnabledAsync(new() { Timeout = ElementVisibilityTimeout });
    }

    [Fact]
    public async Task WhenASubjectIsCreated_ThenItIsStoredUnderTheChosenName()
    {
        // The file is written to the test output directory, which later runs reuse, so it is removed again.
        // File watching is off in testRoot.json, so the created motor stays in the live graph for the rest
        // of the host's life, which is why the name is unique per run.
        var name = $"CreatedMotor{Guid.NewGuid():N}";
        var filePath = Path.Combine(AppContext.BaseDirectory, "TestData", $"{name}.json");
        var isCompleted = false;
        try
        {
            // Arrange
            var page = await _fixture.CreatePageAsync();
            await OpenDialogAsync(page);
            await PickTypeAsync(page, "motor");
            await EnterMotorNameAsync(page);
            await NameInput(page).FillAsync(name);
            await Assertions.Expect(CreateButton(page)).ToBeEnabledAsync(new() { Timeout = ElementVisibilityTimeout });

            // Act
            await CreateButton(page).ClickAsync();

            // Assert
            await Assertions.Expect(Dialog(page)).Not.ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });
            await AsyncTestHelpers.WaitUntilAsync(() => File.Exists(filePath), TimeSpan.FromSeconds(10));

            // The file holds the edits of the motor form only when they were applied before it was written.
            await AsyncTestHelpers.WaitUntilAsync(
                () => FileContains(filePath, MotorName),
                TimeSpan.FromSeconds(10),
                message: "The created file does not contain the motor name that was entered in the form.");
            isCompleted = true;
        }
        finally
        {
            try
            {
                File.Delete(filePath);
            }
            catch (Exception) when (!isCompleted)
            {
                // The failure of the test itself is the one to report.
            }
        }
    }

    [Fact]
    public async Task WhenPickedTypesAreAbandoned_ThenTheirFormSubjectsLeaveTheGraphAndStop()
    {
        // Arrange - motors that exist already are told apart by instance, because the circuit of an earlier
        // test can still be closing and take its own form subject out of the graph while this test runs.
        var registry = _fixture.ServerServices.GetRequiredService<IInterceptorSubjectContext>().GetService<ISubjectRegistry>();
        var existingMotors = registry.KnownSubjects.Keys.OfType<Motor>().ToHashSet();
        Motor[] GetFormMotors() => registry.KnownSubjects.Keys.OfType<Motor>().Where(motor => !existingMotors.Contains(motor)).ToArray();

        var page = await _fixture.CreatePageAsync();
        await OpenDialogAsync(page);

        // Act - the first pick is abandoned with "Back"
        await PickTypeAsync(page, "motor");
        await AsyncTestHelpers.WaitUntilAsync(
            () => GetFormMotors().Length == 1,
            TimeSpan.FromSeconds(10),
            message: "The motor that backs the form was not attached to the graph.");
        var firstMotor = GetFormMotors().Single();
        await AsyncTestHelpers.WaitUntilAsync(
            () => firstMotor.Status == MotorStatus.Running,
            TimeSpan.FromSeconds(10),
            message: "The motor that backs the form did not start.");

        await BackButton(page).ClickAsync();

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => GetFormMotors().Length == 0,
            TimeSpan.FromSeconds(10),
            message: "The motor of the type that was left with Back is still in the graph.");
        await AsyncTestHelpers.WaitUntilAsync(
            () => firstMotor.Status == MotorStatus.Stopped,
            TimeSpan.FromSeconds(10),
            message: "The motor of the type that was left with Back still runs.");

        // Act - the second pick is abandoned with "Cancel"
        await PickTypeAsync(page, "motor");
        await AsyncTestHelpers.WaitUntilAsync(
            () => GetFormMotors().Length == 1,
            TimeSpan.FromSeconds(10),
            message: "The motor that backs the second form was not attached to the graph.");

        await page.Locator("[data-testid='cancel-button']").ClickAsync();

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => GetFormMotors().Length == 0,
            TimeSpan.FromSeconds(10),
            message: "The motor of the dialog that was cancelled is still in the graph.");
    }

    [Fact]
    public async Task WhenCancelIsClicked_ThenTheDialogCloses()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await OpenDialogAsync(page);

        // Act
        await page.Locator("[data-testid='cancel-button']").ClickAsync();

        // Assert
        await Assertions.Expect(Dialog(page)).Not.ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });
    }

    [Fact]
    public async Task WhenHostedSubjectIsAddedToGridWithIsEnabledCleared_ThenItStartsDisabled()
    {
        // Creating the cell rewrites the page file in the test output directory, which later runs reuse.
        // Restoring it only restores the file: file watching is off in testRoot.json, so the created
        // (disabled) store stays in the live graph for the rest of the host's life.
        var gridPagePath = Path.Combine(AppContext.BaseDirectory, "TestData", "Demo", "Grid.md");
        var originalGridPage = await File.ReadAllBytesAsync(gridPagePath);
        var isCompleted = false;
        try
        {
            // Arrange
            var page = await _fixture.CreatePageAsync();
            await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/Grid.md");
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

            // The edit menu is prerendered, and a click before the circuit is connected is lost.
            await Assertions.Expect(page.GetByTitle(new Regex(@"^Automatic \(")))
                .ToBeVisibleAsync(new() { Timeout = PageLoadTimeout });

            var editMenu = page.Locator("[data-testid='edit-mode-menu']");
            await Assertions.Expect(editMenu).ToBeVisibleAsync(new() { Timeout = PageLoadTimeout });
            await editMenu.ClickAsync();
            await page.GetByText("Inline").ClickAsync();

            var emptyCell = page.Locator(".grid-cell.empty").First;
            await Assertions.Expect(emptyCell).ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });
            var cellIndex = await page.Locator(".grid-cell:not(.empty)").CountAsync();
            await emptyCell.ClickAsync();

            await Assertions.Expect(TypeRows(page).First).ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });
            await PickTypeAsync(page, "inmemoryhistorystoresubject");

            // A grid cell has no name, so the dialog must not ask for one.
            await Assertions.Expect(page.Locator("[data-testid='create-name-input']")).ToHaveCountAsync(0);

            var isEnabledCheckBox = page.GetByLabel("Enabled (auto-start on application startup)");
            await Assertions.Expect(isEnabledCheckBox).ToBeCheckedAsync(new() { Timeout = ElementVisibilityTimeout });
            await isEnabledCheckBox.UncheckAsync();
            await Assertions.Expect(isEnabledCheckBox).Not.ToBeCheckedAsync();
            await Assertions.Expect(CreateButton(page)).ToBeEnabledAsync(new() { Timeout = ElementVisibilityTimeout });

            // Act
            await CreateButton(page).ClickAsync();
            await Assertions.Expect(Dialog(page)).Not.ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });

            // Assert - the store reads IsEnabled once when it starts, so a start that saw the constructor
            // default reports Running for good instead of Disabled.
            await page.GotoAsync($"{_fixture.ServerAddress}browser/Demo/Grid.md/grid/Cells/{cellIndex}/Child");
            await Assertions.Expect(page.GetByText("In-Memory History").First)
                .ToBeVisibleAsync(new() { Timeout = PageLoadTimeout });
            await Assertions.Expect(page.GetByText("Status: Disabled"))
                .ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });
            isCompleted = true;
        }
        finally
        {
            try
            {
                await File.WriteAllBytesAsync(gridPagePath, originalGridPage);
            }
            catch (Exception) when (!isCompleted)
            {
                // The failure of the test itself is the one to report.
            }
        }
    }
}
