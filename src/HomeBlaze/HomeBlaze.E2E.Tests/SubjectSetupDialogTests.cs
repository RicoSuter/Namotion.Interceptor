using System.Text.RegularExpressions;
using HomeBlaze.Components;
using HomeBlaze.E2E.Tests.Infrastructure;
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
        await Assertions.Expect(page.Locator("[data-testid='cancel-button']")).ToBeVisibleAsync();
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
        await Assertions.Expect(page.Locator("[data-testid='type-group-devices']")).ToHaveTextAsync(new Regex(@"^\s*Devices\s*0\s*$"));
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
        await page.Locator("[data-testid='type-clear-search']").ClickAsync();

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

        var layoutsGroup = page.Locator("[data-testid='type-group-layouts']");

        // Act
        await layoutsGroup.ClickAsync();

        // Assert
        await Assertions.Expect(TypeRow(page, "motor")).ToHaveCountAsync(0, new() { Timeout = ElementVisibilityTimeout });
        await Assertions.Expect(TypeRow(page, "gridlayout")).ToBeVisibleAsync();
        await Assertions.Expect(TypeRow(page, "canvaslayout")).ToBeVisibleAsync();

        var groupCount = int.Parse(await layoutsGroup.Locator("span").Last.InnerTextAsync());
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
        var layoutsGroup = page.Locator("[data-testid='type-group-layouts']");
        await layoutsGroup.ClickAsync();
        await Assertions.Expect(layoutsGroup).ToHaveAttributeAsync("aria-pressed", "true");

        // Act
        await SearchInput(page).FillAsync("simulated motor");

        // Assert
        await Assertions.Expect(TypeRow(page, "motor")).ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });
        await Assertions.Expect(allGroup).ToHaveAttributeAsync("aria-pressed", "true");
        await Assertions.Expect(layoutsGroup).ToHaveAttributeAsync("aria-pressed", "false");
    }

    [Fact]
    public async Task WhenAGroupIsSelectedDuringASearch_ThenItNarrowsTheMatchesAndSurvivesClearingTheSearch()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await OpenDialogAsync(page);
        var layoutsGroup = page.Locator("[data-testid='type-group-layouts']");
        await SearchInput(page).FillAsync("simulated motor");
        await Assertions.Expect(TypeRow(page, "motor")).ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });

        // Act
        await layoutsGroup.ClickAsync();

        // Assert
        await Assertions.Expect(page.Locator("[data-testid='type-empty-state']"))
            .ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });
        await Assertions.Expect(TypeRows(page)).ToHaveCountAsync(0);

        // Act
        await page.Locator("[data-testid='type-clear-search']").ClickAsync();

        // Assert
        await Assertions.Expect(TypeRow(page, "gridlayout")).ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });
        await Assertions.Expect(TypeRow(page, "motor")).ToHaveCountAsync(0);
        await Assertions.Expect(layoutsGroup).ToHaveAttributeAsync("aria-pressed", "true");
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
        await Assertions.Expect(Dialog(page).GetByText(".json", new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(Dialog(page).GetByText("Part of the subject's path, which is its ID.")).ToBeVisibleAsync();

        // The suggested name is valid as it is, so the motor's own required name is all that Create waits for.
        await Assertions.Expect(CreateButton(page)).ToBeDisabledAsync(new() { Timeout = ElementVisibilityTimeout });

        // Act
        await EnterMotorNameAsync(page);

        // Assert
        await Assertions.Expect(CreateButton(page)).ToBeEnabledAsync(new() { Timeout = ElementVisibilityTimeout });
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

        // Act
        await page.Locator("[data-testid='back-button']").ClickAsync();

        // Assert
        await Assertions.Expect(DialogTitle(page)).ToHaveTextAsync("Create subject");
        await Assertions.Expect(SearchInput(page)).ToHaveValueAsync("history");
        await Assertions.Expect(CreateButton(page)).ToHaveCountAsync(0);

        // Act
        await PickTypeAsync(page, "sqlitehistorystoresubject");

        // Assert
        await Assertions.Expect(DialogTitle(page)).ToHaveTextAsync("Create SQLite history");
        await Assertions.Expect(NameInput(page)).ToHaveValueAsync("SqliteHistoryStoreSubject");
    }

    [Fact]
    public async Task WhenARequiredFieldIsEmpty_ThenCreateIsDisabled()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await OpenDialogAsync(page);

        // Act
        await PickTypeAsync(page, "shellydevice");

        // Assert
        await Assertions.Expect(CreateButton(page)).ToBeDisabledAsync(new() { Timeout = ElementVisibilityTimeout });

        // Act
        await page.GetByLabel("Host Address (IP or hostname)").FillAsync("192.0.2.1");

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
        var helperText = Dialog(page).Locator(".mud-input-helper-text");

        // Act & Assert - the test root already contains History.json
        await NameInput(page).FillAsync("history");
        await Assertions.Expect(helperText.Filter(new() { HasText = "A subject with this name already exists" }))
            .ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });
        await Assertions.Expect(CreateButton(page)).ToBeDisabledAsync();

        await NameInput(page).FillAsync("invalid/name");
        await Assertions.Expect(helperText.Filter(new() { HasText = "Name contains invalid characters" }))
            .ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });
        await Assertions.Expect(CreateButton(page)).ToBeDisabledAsync();

        await NameInput(page).FillAsync("");
        await Assertions.Expect(helperText.Filter(new() { HasText = "Name is required" }))
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
        }
        finally
        {
            File.Delete(filePath);
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

        await page.Locator("[data-testid='back-button']").ClickAsync();

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
        }
        finally
        {
            await File.WriteAllBytesAsync(gridPagePath, originalGridPage);
        }
    }
}
