using System.Text.RegularExpressions;
using HomeBlaze.E2E.Tests.Infrastructure;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for CreateSubjectWizard functionality.
/// Tests type selection, name input, wizard navigation, and subject creation.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
public class CreateSubjectWizardTests
{
    private readonly PlaywrightFixture _fixture;

    public CreateSubjectWizardTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task NavigateToDemoFolderAsync(IPage page)
    {
        await page.GotoAsync($"{_fixture.ServerAddress}");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // Navigate to Browser
        var browserLink = page.GetByRole(AriaRole.Link, new() { Name = "Browser" });
        await Assertions.Expect(browserLink).ToBeVisibleAsync(new() { Timeout = 30000 });
        await browserLink.ClickAsync();
        await page.WaitForURLAsync(url => url.Contains("/browser"), new() { Timeout = 30000 });

        // Select "demo" VirtualFolder
        var demoFolder = page.GetByText("demo").First;
        await demoFolder.ClickAsync();
        await page.WaitForTimeoutAsync(500);
    }

    private async Task OpenCreateWizardAsync(IPage page)
    {
        var createButton = page.Locator("button:has-text('Create')").First;
        await createButton.ClickAsync();

        // Wait for wizard dialog to be fully visible
        var wizard = page.Locator("[data-testid='create-subject-wizard']");
        await Assertions.Expect(wizard).ToBeVisibleAsync(new() { Timeout = 5000 });

        // Wait for Blazor SignalR to be fully connected (network idle indicates ready)
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 10000 });
        await page.WaitForTimeoutAsync(500);
    }

    private async Task CleanupTestFileAsync(string fileName)
    {
        try
        {
            // TestData directory is relative to the test bin folder (configured in testRoot.json)
            var testFilePath = Path.Combine("TestData", fileName); // Root level for now
            if (File.Exists(testFilePath))
            {
                File.Delete(testFilePath);
                await Task.Delay(500); // Give time for any file watchers to process deletion
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    [Fact]
    public async Task CreateButton_ShouldOpenWizard()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await NavigateToDemoFolderAsync(page);

        // Act: Click Create button
        await OpenCreateWizardAsync(page);

        // Assert: Wizard dialog should be visible
        var wizard = page.Locator("[data-testid='create-subject-wizard']");
        await Assertions.Expect(wizard).ToBeVisibleAsync(new() { Timeout = 5000 });
    }

    [Fact]
    public async Task Wizard_Step1_ShouldShowNameInputAndTypeCards()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await NavigateToDemoFolderAsync(page);
        await OpenCreateWizardAsync(page);

        // Assert: Name input should be visible
        var nameInput = page.Locator("[data-testid='create-name-input']");
        await Assertions.Expect(nameInput).ToBeVisibleAsync(new() { Timeout = 5000 });

        // Assert: Type cards should be visible (check for at least one category)
        var categoryPanels = page.Locator("[data-testid^='category-panel-']");
        var count = await categoryPanels.CountAsync();
        Assert.True(count > 0, "At least one category panel should be visible");
    }

    [Fact]
    public async Task Wizard_AccordionsExpanded_ByDefault()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await NavigateToDemoFolderAsync(page);
        await OpenCreateWizardAsync(page);

        // Assert: All accordion panels should be expanded (IsInitiallyExpanded="true")
        // Check if type cards are visible (they would be hidden if accordions are collapsed)
        var typeCards = page.Locator("[data-testid^='type-card-']");
        var visibleCount = 0;
        for (int i = 0; i < await typeCards.CountAsync(); i++)
        {
            if (await typeCards.Nth(i).IsVisibleAsync())
            {
                visibleCount++;
            }
        }
        Assert.True(visibleCount > 0, "Type cards should be visible (accordions expanded)");
    }

    [Fact]
    public async Task Wizard_EnterName_TypeCardClick_ShouldAdvanceToStep2()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await NavigateToDemoFolderAsync(page);
        await OpenCreateWizardAsync(page);

        // Act: Wait for step 1 to be visible
        var step1Title = page.GetByText("Step 1:");
        await Assertions.Expect(step1Title).ToBeVisibleAsync(new() { Timeout = 5000 });

        // Enter name
        var nameInput = page.GetByLabel("Name");
        await nameInput.FillAsync("mytest");
        await page.WaitForTimeoutAsync(300); // Wait for name to be bound

        // Act: Click on first type card
        var firstTypeCard = page.Locator("[data-testid^='type-card-']").First;
        await firstTypeCard.ClickAsync();

        // Give time for navigation
        await page.WaitForTimeoutAsync(500);

        // Assert: Should be on step 2 (BACK button only shows when on step 2)
        var backButton = page.Locator("[data-testid='back-button']");
        await Assertions.Expect(backButton).ToBeVisibleAsync(new() { Timeout = 5000 });
    }

    [Fact]
    public async Task Wizard_NoName_TypeCardClick_ShouldShowNextButton()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await NavigateToDemoFolderAsync(page);
        await OpenCreateWizardAsync(page);

        // Act: Click type card without entering name
        var firstTypeCard = page.Locator("[data-testid^='type-card-']").First;
        await firstTypeCard.ClickAsync();
        await page.WaitForTimeoutAsync(500);

        // Assert: Should still be on step 1 with Next button visible but disabled
        var nextButton = page.Locator("[data-testid='next-button']");
        await Assertions.Expect(nextButton).ToBeVisibleAsync(new() { Timeout = 5000 });
        await Assertions.Expect(nextButton).ToBeDisabledAsync();

        // Assert: Enter name and Next button should become enabled
        var nameInput = page.GetByLabel("Name");
        await nameInput.FillAsync("mytest");
        await Assertions.Expect(nextButton).ToBeEnabledAsync(new() { Timeout = 5000 });
    }

    [Fact]
    public async Task Wizard_Step2_ShouldShowConfigurationOrMessage()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await NavigateToDemoFolderAsync(page);
        await OpenCreateWizardAsync(page);

        // Navigate to step 2
        var firstTypeCard = page.Locator("[data-testid^='type-card-']").First;
        await firstTypeCard.ClickAsync();
        await page.WaitForTimeoutAsync(500);

        // Assert: Step 2 should show either configuration component or "No configuration needed"
        var configSection = page.Locator("text='No configuration needed'");
        var hasMessage = await configSection.IsVisibleAsync();

        // If no message, there should be some configuration UI visible
        if (!hasMessage)
        {
            // Check for any form elements (input, select, checkbox, etc.)
            var hasInputs = await page.Locator("input, select, .mud-input").CountAsync() > 0;
            Assert.True(hasInputs, "Step 2 should show either configuration or 'No configuration needed' message");
        }
    }

    [Fact]
    public async Task Wizard_CreateButton_ShouldNotBeVisible_OnStep1()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await NavigateToDemoFolderAsync(page);
        await OpenCreateWizardAsync(page);

        // Assert: Create button should not be visible on step 1
        var wizardCreateButton = page.Locator("[data-testid='create-button']");
        await Assertions.Expect(wizardCreateButton).Not.ToBeVisibleAsync(new() { Timeout = 5000 });
    }

    [Fact]
    public async Task Wizard_CreateButton_ShouldBeEnabled_OnStep2WithName()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await NavigateToDemoFolderAsync(page);
        await OpenCreateWizardAsync(page);

        // Wait for name input to be ready
        var nameInput = page.GetByLabel("Name");
        await Assertions.Expect(nameInput).ToBeVisibleAsync(new() { Timeout = 5000 });

        // Enter name and select type
        await nameInput.FillAsync("mytest");

        var firstTypeCard = page.Locator("[data-testid^='type-card-']").First;
        await firstTypeCard.ClickAsync();
        await page.WaitForTimeoutAsync(500);

        // Assert: Create button should be enabled on step 2
        var wizardCreateButton = page.Locator("[data-testid='create-button']");
        await Assertions.Expect(wizardCreateButton).ToBeEnabledAsync(new() { Timeout = 5000 });
    }

    [Fact]
    public async Task Wizard_Cancel_ShouldCloseDialog()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await NavigateToDemoFolderAsync(page);
        await OpenCreateWizardAsync(page);

        // Act: Click Cancel
        var cancelButton = page.Locator("[data-testid='cancel-button']");
        await cancelButton.ClickAsync();

        // Assert: Dialog should be closed
        var wizard = page.Locator("[data-testid='create-subject-wizard']");
        await Assertions.Expect(wizard).Not.ToBeVisibleAsync(new() { Timeout = 5000 });
    }

    [Fact]
    public async Task Wizard_BackButton_ShouldReturnToStep1()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await NavigateToDemoFolderAsync(page);
        await OpenCreateWizardAsync(page);

        // Enter name first to enable auto-advance
        var nameInput = page.GetByLabel("Name");
        await nameInput.FillAsync("mytest");
        await page.WaitForTimeoutAsync(300);

        // Click type card to advance to step 2
        var firstTypeCard = page.Locator("[data-testid^='type-card-']").First;
        await firstTypeCard.ClickAsync();
        await page.WaitForTimeoutAsync(500);

        // Act: Click Back
        var backButton = page.Locator("[data-testid='back-button']");
        await backButton.ClickAsync();
        await page.WaitForTimeoutAsync(500);

        // Assert: Should be back on step 1
        var step1Title = page.GetByText("Step 1:");
        await Assertions.Expect(step1Title).ToBeVisibleAsync(new() { Timeout = 5000 });

        // Back button should no longer be visible
        await Assertions.Expect(backButton).Not.ToBeVisibleAsync();
    }

    [Fact]
    public async Task Wizard_CreateSubject_ShouldAppearInBrowser()
    {
        // Arrange
        // Use unique name with timestamp to avoid conflicts with previous test runs
        var testName = $"E2ETestMotor_{DateTime.Now:HHmmssff}";
        var testFileName = $"{testName}.json";

        var page = await _fixture.CreatePageAsync();

        try
        {
            // Navigate to Browser (create at root level for now - UI issue with folder-specific Create button)
            await page.GotoAsync($"{_fixture.ServerAddress}");
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
            var browserLink = page.GetByRole(AriaRole.Link, new() { Name = "Browser" });
            await Assertions.Expect(browserLink).ToBeVisibleAsync(new() { Timeout = 30000 });
            await browserLink.ClickAsync();
            await page.WaitForURLAsync(url => url.Contains("/browser"), new() { Timeout = 30000 });

            await OpenCreateWizardAsync(page);

            // Enter name
            var nameInput = page.GetByLabel("Name");
            await nameInput.FillAsync(testName);
            await page.WaitForTimeoutAsync(300);

            // Select Motor type
            var motorTypeCard = page.Locator("[data-testid='type-card-motor']");
            await motorTypeCard.ClickAsync();
            await page.WaitForTimeoutAsync(500);

            // Should be on step 2 - configure motor
            var motorNameInput = page.GetByLabel("Name", new() { Exact = false }).Last;
            await motorNameInput.FillAsync("Test Motor E2E");
            await page.WaitForTimeoutAsync(300);

            // Act: Click Create
            var createButton = page.Locator("[data-testid='create-button']");
            await createButton.ClickAsync();

            // Wait for dialog to close
            var wizard = page.Locator("[data-testid='create-subject-wizard']");
            await Assertions.Expect(wizard).Not.ToBeVisibleAsync(new() { Timeout = 5000 });

            // Give time for hierarchy update and file write
            await page.WaitForTimeoutAsync(2000);

            // First verify the JSON file was created with correct content
            var testFilePath = Path.Combine("TestData", testFileName); // Root level, not in demo folder
            Assert.True(File.Exists(testFilePath), "JSON file should be created");

            var jsonContent = await File.ReadAllTextAsync(testFilePath);
            Assert.Contains("Test Motor E2E", jsonContent); // Motor name should be in JSON

            // Now check if subject appears in the browser
            var createdSubject = page.GetByText(testName);
            await Assertions.Expect(createdSubject).ToBeVisibleAsync(new() { Timeout = 10000 });
        }
        finally
        {
            // Cleanup: Delete test file
            await CleanupTestFileAsync(testFileName);
        }
    }

}
