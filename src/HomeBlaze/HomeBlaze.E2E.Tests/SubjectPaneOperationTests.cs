using HomeBlaze.E2E.Tests.Infrastructure;
using HomeBlaze.E2E.Tests.Models;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for running the operations and queries of a subject from its pane in the subject browser.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class SubjectPaneOperationTests
{
    private const int PageLoadTimeout = 30000;
    private const string MotorPath = "Demo/Motor";

    private readonly PlaywrightFixture _fixture;

    public SubjectPaneOperationTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Opens the subject browser at a subject, given by its path, and returns the pane of that subject.
    /// </summary>
    private async Task<(IPage Page, ILocator Pane)> OpenPaneAsync(string subjectPath)
    {
        var page = await _fixture.CreatePageAsync();
        await page.SetViewportSizeAsync(1280, 720);
        await page.GotoAsync($"{_fixture.ServerAddress}browser/{subjectPath}");

        // The page is prerendered, and a click before its circuit is interactive is lost.
        await page.WaitUntilInteractiveAsync(PageLoadTimeout);
        return (page, page.Locator("#scrollContainer [data-testid='subject-pane']").Last);
    }

    private static ILocator Method(ILocator pane, string title) =>
        pane.GetByRole(AriaRole.Button, new() { Name = title, Exact = true });

    [Fact]
    public async Task WhenAQueryIsRun_ThenItsResultIsShownAndItsButtonIsEnabledAgain()
    {
        // Arrange
        var (page, pane) = await OpenPaneAsync(MotorPath);
        var query = Method(pane, "Get Diagnostics");
        await Assertions.Expect(query).ToBeEnabledAsync(new() { Timeout = PageLoadTimeout });

        // Act
        await query.ClickAsync();

        // Assert
        var dialog = page.Locator(".mud-dialog");
        await Assertions.Expect(dialog).ToContainTextAsync("completed successfully", new() { Timeout = PageLoadTimeout });
        await Assertions.Expect(dialog).ToContainTextAsync("SpeedDelta");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "OK", Exact = true }).ClickAsync();
        await Assertions.Expect(dialog).ToHaveCountAsync(0);
        await Assertions.Expect(query).ToBeEnabledAsync();
    }

    [Fact]
    public async Task WhenAFolderPaneIsShown_ThenItHasNoButtonsForTheMethodsOnlyForAgents()
    {
        // Arrange & Act
        var (_, pane) = await OpenPaneAsync("Demo");

        // Assert
        await Assertions.Expect(Method(pane, "Create")).ToBeVisibleAsync(new() { Timeout = PageLoadTimeout });
        await Assertions.Expect(Method(pane, "Get Order")).ToHaveCountAsync(0);
        await Assertions.Expect(Method(pane, "Set Order")).ToHaveCountAsync(0);
        await Assertions.Expect(Method(pane, "Get Metadata")).ToHaveCountAsync(0);
        await Assertions.Expect(Method(pane, "Set Metadata")).ToHaveCountAsync(0);
        await Assertions.Expect(Method(pane, "Set Child Metadata")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task WhenAConfirmationIsCanceled_ThenTheButtonIsEnabledAgain()
    {
        // Arrange
        var (page, pane) = await OpenPaneAsync(MotorPath);
        var operation = Method(pane, "Restart Controller");
        await Assertions.Expect(operation).ToBeEnabledAsync(new() { Timeout = PageLoadTimeout });

        // Act
        await operation.ClickAsync();
        var dialog = page.Locator(".mud-dialog");
        await Assertions.Expect(dialog).ToContainTextAsync("Restarts the motor controller", new() { Timeout = PageLoadTimeout });
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();

        // Assert
        await Assertions.Expect(dialog).ToHaveCountAsync(0);
        await Assertions.Expect(operation).ToBeEnabledAsync();
    }

    [Fact]
    public async Task WhenAnOperationThrowsOperationCanceledException_ThenItsErrorIsShown()
    {
        // Arrange
        var (name, subject) = await _fixture.AddSubjectToRootAsync<FailingOperationSubject>();
        try
        {
            var (page, pane) = await OpenPaneAsync(name);
            var operation = Method(pane, "Time Out");
            await Assertions.Expect(operation).ToBeEnabledAsync(new() { Timeout = PageLoadTimeout });

            // Act
            await operation.ClickAsync();

            // Assert
            var dialog = page.Locator(".mud-dialog");
            await Assertions.Expect(dialog).ToContainTextAsync("Time Out failed with an error.", new() { Timeout = PageLoadTimeout });
            await Assertions.Expect(dialog).ToContainTextAsync("The request timed out.");
            await dialog.GetByRole(AriaRole.Button, new() { Name = "OK", Exact = true }).ClickAsync();
            await Assertions.Expect(dialog).ToHaveCountAsync(0);
            await Assertions.Expect(operation).ToBeEnabledAsync();
        }
        finally
        {
            _fixture.RemoveSubjectFromRoot(name, subject);
        }
    }

    [Fact]
    public async Task WhenAnErrorDialogIsShownInDarkMode_ThenItsAlertHasTheLightenedErrorTone()
    {
        // Arrange: the app starts in the dark theme.
        var (name, subject) = await _fixture.AddSubjectToRootAsync<FailingOperationSubject>();
        try
        {
            var (page, pane) = await OpenPaneAsync(name);
            var operation = Method(pane, "Time Out");
            await Assertions.Expect(operation).ToBeEnabledAsync(new() { Timeout = PageLoadTimeout });

            // Act
            await operation.ClickAsync();

            // Assert: the dialog renders outside the layout, and still gets the styles of the mode.
            var alert = page.Locator(".mud-dialog .mud-alert-text-error");
            await Assertions.Expect(alert).ToContainTextAsync("The request timed out.", new() { Timeout = PageLoadTimeout });
            var expected = await page.ResolveColorAsync("var(--mud-palette-error-lighten)");
            Assert.NotEqual(await page.ResolveColorAsync("var(--mud-palette-error-darken)"), expected);
            await Assertions.Expect(alert.Locator(".mud-alert-message")).ToHaveCSSAsync("color", expected);
        }
        finally
        {
            _fixture.RemoveSubjectFromRoot(name, subject);
        }
    }
}
