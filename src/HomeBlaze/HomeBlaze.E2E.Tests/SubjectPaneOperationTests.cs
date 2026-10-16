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
    private const string MotorPath = "browser/Demo/Motor";

    // The page is prerendered, and a click before its circuit is interactive is lost. The pane width is set by the
    // script the interactive browser runs, so it marks the point from which clicks arrive.
    private const string BrowserIsInteractive =
        "() => (document.getElementById('scrollContainer')?.style.getPropertyValue('--pane-width') ?? '') !== ''";

    private readonly PlaywrightFixture _fixture;

    public SubjectPaneOperationTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<(IPage Page, ILocator Pane)> OpenMotorPaneAsync()
    {
        var page = await _fixture.CreatePageAsync();
        await page.SetViewportSizeAsync(1280, 720);
        await page.GotoAsync($"{_fixture.ServerAddress}{MotorPath}");
        await page.WaitForFunctionAsync(BrowserIsInteractive, null, new() { Timeout = PageLoadTimeout });
        return (page, page.Locator("#scrollContainer [data-testid='subject-pane']").Last);
    }

    private static ILocator Method(ILocator pane, string title) =>
        pane.GetByRole(AriaRole.Button, new() { Name = title, Exact = true });

    [Fact]
    public async Task WhenAQueryIsRun_ThenItsResultIsShownAndItsButtonIsEnabledAgain()
    {
        // Arrange
        var (page, pane) = await OpenMotorPaneAsync();
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
    public async Task WhenAConfirmationIsCancelled_ThenTheButtonIsEnabledAgain()
    {
        // Arrange
        var (page, pane) = await OpenMotorPaneAsync();
        var operation = Method(pane, "Emergency Stop");
        await Assertions.Expect(operation).ToBeEnabledAsync(new() { Timeout = PageLoadTimeout });

        // Act
        await operation.ClickAsync();
        var dialog = page.Locator(".mud-dialog");
        await Assertions.Expect(dialog).ToContainTextAsync("Immediately stops the motor", new() { Timeout = PageLoadTimeout });
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
            var page = await _fixture.CreatePageAsync();
            await page.SetViewportSizeAsync(1280, 720);
            await page.GotoAsync($"{_fixture.ServerAddress}browser/{name}");
            await page.WaitForFunctionAsync(BrowserIsInteractive, null, new() { Timeout = PageLoadTimeout });
            var pane = page.Locator("#scrollContainer [data-testid='subject-pane']").Last;
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
            var page = await _fixture.CreatePageAsync();
            await page.SetViewportSizeAsync(1280, 720);
            await page.GotoAsync($"{_fixture.ServerAddress}browser/{name}");
            await page.WaitForFunctionAsync(BrowserIsInteractive, null, new() { Timeout = PageLoadTimeout });
            var pane = page.Locator("#scrollContainer [data-testid='subject-pane']").Last;
            var operation = Method(pane, "Time Out");
            await Assertions.Expect(operation).ToBeEnabledAsync(new() { Timeout = PageLoadTimeout });

            // Act
            await operation.ClickAsync();

            // Assert: the dialog renders outside the layout, and still gets the styles of the mode.
            var alert = page.Locator(".mud-dialog .mud-alert-text-error");
            await Assertions.Expect(alert).ToContainTextAsync("The request timed out.", new() { Timeout = PageLoadTimeout });
            var expected = await page.ResolveColourAsync("var(--mud-palette-error-lighten)");
            Assert.NotEqual(await page.ResolveColourAsync("var(--mud-palette-error-darken)"), expected);
            await Assertions.Expect(alert.Locator(".mud-alert-message")).ToHaveCSSAsync("color", expected);
        }
        finally
        {
            _fixture.RemoveSubjectFromRoot(name, subject);
        }
    }
}
