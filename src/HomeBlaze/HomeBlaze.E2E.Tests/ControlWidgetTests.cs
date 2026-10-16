using System.Globalization;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using HomeBlaze.E2E.Tests.Infrastructure;
using HomeBlaze.Samples;
using HomeBlaze.Storage.Files;
using Microsoft.Playwright;
using Namotion.Interceptor;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for the control widgets on markdown pages: what they write, which operations they invoke and what they save.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class ControlWidgetTests
{
    private const int Timeout = 30000;
    private const string PageName = "Controls.md";
    private const string MotorName = "ctlmotor";
    private const string CardHeadLine = ":: HomeBlaze.Components.Card#ctlcard";
    private const string MotorHeadLine = ":: HomeBlaze.Samples.Motor#ctlmotor";

    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);
    private static readonly Regex NotSelectable = new("subject-path-picker-not-selectable");

    private readonly PlaywrightFixture _fixture;

    public ControlWidgetTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    // A save of the page parses it again, which can put a new motor in the place of the old one, so it is looked up each time.
    private Motor GetMotor() =>
        (Motor)((MarkdownFile)_fixture.GetDemoFolder().Children[PageName]).Children[MotorName];

    private void ResetMotor()
    {
        var motor = GetMotor();
        motor.IsPowered = true;
        motor.Mode = MotorMode.Normal;
        motor.Load = 0.5;
    }

    private async Task<IPage> OpenPageAsync()
    {
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/{PageName}");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // The controls are prerendered, and a click before the circuit is connected is lost.
        await page.WaitUntilInteractiveAsync(Timeout);
        return page;
    }

    private static ILocator GetSwitch(IPage page, string label) =>
        page.GetByRole(AriaRole.Switch, new() { Name = label, Exact = true });

    private static ILocator GetSlider(IPage page, string label) =>
        page.GetByRole(AriaRole.Slider, new() { Name = label, Exact = true });

    private static ILocator GetButton(IPage page, string text) =>
        page.GetByRole(AriaRole.Button, new() { Name = text, Exact = true });

    private static ILocator GetSelect(IPage page, string label) =>
        page.GetByTestId("select").Filter(new() { HasText = label });

    // The class that tells whether an item can be selected is on the element that holds the row of the item and its children.
    private static ILocator GetPickerItem(ILocator picker, string name) =>
        picker.Locator(".mud-treeview-item-content")
            .Filter(new() { Has = picker.Page.GetByText(name, new() { Exact = true }) })
            .Locator("xpath=..");

    // The server reads a page again after it wrote it, and a file that it holds open is read again later.
    private static string? TryReadContent(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (IOException)
        {
            return null;
        }
    }

    // Like File.ReadAllLines: a line break at the end of the file ends the last line and starts no new one.
    private static string[] SplitLines(string content)
    {
        var lines = content.ReplaceLineEndings("\n").Split('\n');
        return lines[^1].Length == 0 ? lines[..^1] : lines;
    }

    /// <summary>
    /// Asserts that a save changed one line of the page, the head line of the block with this name, and that the
    /// line now holds the argument.
    /// </summary>
    private static void AssertOnlyTheHeadLineChanged(string original, string saved, string blockName, string expectedArgument)
    {
        var originalLines = SplitLines(original);
        var savedLines = SplitLines(saved);
        Assert.Equal(originalLines.Length, savedLines.Length);

        var changedIndex = Assert.Single(Enumerable.Range(0, originalLines.Length), index => originalLines[index] != savedLines[index]);
        Assert.Matches($"^:+ \\S+#{blockName}( |$)", originalLines[changedIndex]);
        Assert.Contains($" {expectedArgument}", savedLines[changedIndex]);
    }

    [Fact]
    public async Task WhenSwitchOfAConfigurationPropertyIsToggled_ThenOnlyTheHeadLineOfItsBlockIsSaved()
    {
        // Arrange
        var pagePath = _fixture.GetDemoPagePath(PageName);
        var originalContent = await _fixture.ReadFileAsync(pagePath);

        try
        {
            ResetMotor();
            var page = await OpenPageAsync();
            var outlineSwitch = GetSwitch(page, "Card outline");
            await Assertions.Expect(outlineSwitch).ToBeCheckedAsync(new() { Timeout = Timeout });

            // Act
            await outlineSwitch.ClickAsync();

            // Assert: the page is saved a moment after the write, so the file is read until it has the new value.
            await Assertions.Expect(outlineSwitch).Not.ToBeCheckedAsync(new() { Timeout = Timeout });

            var savedContent = string.Empty;
            await AsyncTestHelpers.WaitUntilAsync(
                () => TryReadContent(pagePath) is { } content &&
                    SplitLines(savedContent = content).Any(line =>
                        line.StartsWith(CardHeadLine, StringComparison.Ordinal) &&
                        line.Contains("Outlined=false", StringComparison.Ordinal)),
                WaitTimeout,
                message: "The page was not saved with the outline of the card switched off");

            AssertOnlyTheHeadLineChanged(originalContent, savedContent, "ctlcard", "Outlined=false");
        }
        finally
        {
            await _fixture.WriteDemoPageAsync(PageName, originalContent);
        }
    }

    [Fact]
    public async Task WhenSwitchOfARuntimePropertyIsToggled_ThenThePropertyIsWritten()
    {
        try
        {
            // Arrange
            ResetMotor();
            var page = await OpenPageAsync();
            var powerSwitch = GetSwitch(page, "Power");
            await Assertions.Expect(powerSwitch).ToBeCheckedAsync(new() { Timeout = Timeout });

            // Act
            await powerSwitch.ClickAsync();

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(() => !GetMotor().IsPowered, WaitTimeout, message: "The switch did not power the motor off");
            await Assertions.Expect(powerSwitch).Not.ToBeCheckedAsync(new() { Timeout = Timeout });
        }
        finally
        {
            ResetMotor();
        }
    }

    [Fact]
    public async Task WhenSwitchWithOperationsIsToggled_ThenTheOperationOfTheNewStateIsInvoked()
    {
        try
        {
            // Arrange
            ResetMotor();
            var page = await OpenPageAsync();
            var operationSwitch = GetSwitch(page, "Power by operation");
            await Assertions.Expect(operationSwitch).ToBeCheckedAsync(new() { Timeout = Timeout });

            // Act: off through PowerOff, then on again through PowerOn.
            await operationSwitch.ClickAsync();
            await AsyncTestHelpers.WaitUntilAsync(() => !GetMotor().IsPowered, WaitTimeout, message: "PowerOff was not invoked");
            await Assertions.Expect(operationSwitch).Not.ToBeCheckedAsync(new() { Timeout = Timeout });
            await Assertions.Expect(operationSwitch).ToBeEnabledAsync(new() { Timeout = Timeout });
            await operationSwitch.ClickAsync();

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(() => GetMotor().IsPowered, WaitTimeout, message: "PowerOn was not invoked");
            await Assertions.Expect(operationSwitch).ToBeCheckedAsync(new() { Timeout = Timeout });
        }
        finally
        {
            ResetMotor();
        }
    }

    [Fact]
    public async Task WhenTheOperationThatASwitchWouldInvokeIsNotEnabled_ThenTheSwitchIsDisabled()
    {
        // Arrange: the blocked switch turns off through PowerOn, which is enabled only while the motor is off, and on
        // through PowerOff, which is enabled only while it is on, so the operation it would invoke is never enabled.
        ResetMotor();

        // Act
        var page = await OpenPageAsync();

        // Assert
        await Assertions.Expect(GetSwitch(page, "Power by operation")).ToBeEnabledAsync(new() { Timeout = Timeout });
        await Assertions.Expect(GetSwitch(page, "Blocked power")).ToBeDisabledAsync(new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenConfirmationOfTheOperationOfASwitchIsCancelled_ThenTheSwitchKeepsItsState()
    {
        try
        {
            // Arrange
            ResetMotor();
            var page = await OpenPageAsync();
            var stopSwitch = GetSwitch(page, "Power with stop");
            await Assertions.Expect(stopSwitch).ToBeCheckedAsync(new() { Timeout = Timeout });

            // Act
            await stopSwitch.ClickAsync();
            var dialog = page.GetByRole(AriaRole.Dialog);
            await Assertions.Expect(dialog).ToContainTextAsync("Confirm: Emergency Stop", new() { Timeout = Timeout });
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();

            // Assert
            await Assertions.Expect(dialog).ToHaveCountAsync(0, new() { Timeout = Timeout });
            await Assertions.Expect(stopSwitch).ToBeCheckedAsync(new() { Timeout = Timeout });
            await Assertions.Expect(stopSwitch).ToBeEnabledAsync(new() { Timeout = Timeout });
            Assert.True(GetMotor().IsPowered);
        }
        finally
        {
            ResetMotor();
        }
    }

    [Fact]
    public async Task WhenSliderIsDraggedAndReleased_ThenThePropertyIsWrittenOnceAndTheValueSurvivesAReload()
    {
        // Arrange
        var pagePath = _fixture.GetDemoPagePath(PageName);
        var originalContent = await _fixture.ReadFileAsync(pagePath);
        var motor = GetMotor();
        var originalSpeed = motor.TargetSpeed;
        var writes = 0;
        using var subscription = ((IInterceptorSubject)motor).Context.GetPropertyChangeObservable()
            .Where(change => ReferenceEquals(change.Property.Subject, motor) && change.Property.Name == nameof(Motor.TargetSpeed))
            .Subscribe(_ => Interlocked.Increment(ref writes));

        try
        {
            var page = await OpenPageAsync();
            var slider = GetSlider(page, "Target speed");
            await Assertions.Expect(slider).ToHaveValueAsync(originalSpeed.ToString(CultureInfo.InvariantCulture), new() { Timeout = Timeout });
            // The pointer acts on the window, so the slider is moved into it first.
            await slider.ScrollIntoViewIfNeededAsync();
            var box = await slider.BoundingBoxAsync() ?? throw new InvalidOperationException("The slider has no box");
            var middle = box.Y + box.Height / 2;

            // Act: the pointer goes down at a quarter of the track and comes up at three quarters, with moves in between.
            await page.Mouse.MoveAsync(box.X + box.Width / 4, middle);
            await page.Mouse.DownAsync();
            await page.Mouse.MoveAsync(box.X + box.Width * 3 / 4, middle, new() { Steps = 10 });
            await page.Mouse.UpAsync();

            // Assert: the page is saved a moment after the write, by which time any further write would have happened.
            await AsyncTestHelpers.WaitUntilAsync(() => GetMotor().TargetSpeed != originalSpeed, WaitTimeout, message: "The slider did not write the target speed");
            var writtenSpeed = GetMotor().TargetSpeed;
            var savedContent = string.Empty;
            await AsyncTestHelpers.WaitUntilAsync(
                () => TryReadContent(pagePath) is { } content &&
                    SplitLines(savedContent = content).Any(line =>
                        line.StartsWith(MotorHeadLine, StringComparison.Ordinal) &&
                        line.Contains($"TargetSpeed={writtenSpeed} ", StringComparison.Ordinal)),
                WaitTimeout,
                message: "The page was not saved with the new target speed");
            AssertOnlyTheHeadLineChanged(originalContent, savedContent, MotorName, $"TargetSpeed={writtenSpeed}");
            Assert.Equal(1, Volatile.Read(ref writes));

            await page.ReloadAsync();
            await Assertions.Expect(GetSlider(page, "Target speed"))
                .ToHaveValueAsync(writtenSpeed.ToString(CultureInfo.InvariantCulture), new() { Timeout = Timeout });
        }
        finally
        {
            await _fixture.WriteDemoPageAsync(PageName, originalContent);
        }
    }

    [Fact]
    public async Task WhenSliderShowsAPercentProperty_ThenItRunsFromZeroToHundredAndWritesAFraction()
    {
        try
        {
            // Arrange
            ResetMotor();
            var page = await OpenPageAsync();
            var slider = GetSlider(page, "Load");
            await Assertions.Expect(slider).ToHaveAttributeAsync("max", "100", new() { Timeout = Timeout });
            await Assertions.Expect(slider).ToHaveValueAsync("50", new() { Timeout = Timeout });

            // Act
            await slider.FocusAsync();
            await page.Keyboard.PressAsync("ArrowRight");

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(() => Math.Abs(GetMotor().Load - 0.51) < 1e-9, WaitTimeout, message: "The slider did not write 0.51");
            await Assertions.Expect(slider).ToHaveValueAsync("51", new() { Timeout = Timeout });
        }
        finally
        {
            ResetMotor();
        }
    }

    [Fact]
    public async Task WhenThePathOfASliderLeadsNowhere_ThenItIsDisabledWithADashAndNoError()
    {
        // Act
        var page = await OpenPageAsync();

        // Assert
        var slider = page.GetByTestId("slider").Filter(new() { HasText = "Wrong slider" });
        await Assertions.Expect(slider.GetByTestId("slider-value")).ToHaveTextAsync("–", new() { Timeout = Timeout });
        await Assertions.Expect(GetSlider(page, "Wrong slider")).ToBeDisabledAsync();
        await Assertions.Expect(page.GetByTestId("widget-argument-error")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task WhenAValueIsSelected_ThenTheEnumPropertyIsWritten()
    {
        try
        {
            // Arrange
            ResetMotor();
            var page = await OpenPageAsync();
            var select = GetSelect(page, "Motor mode");

            // Act
            await select.Locator(".mud-input-control").ClickAsync();
            await page.Locator(".mud-popover-open").GetByText("Boost", new() { Exact = true }).ClickAsync();

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(() => GetMotor().Mode == MotorMode.Boost, WaitTimeout, message: "The select did not write Boost");
        }
        finally
        {
            ResetMotor();
        }
    }

    [Fact]
    public async Task WhenOperationButtonAsksForConfirmationAndItIsGiven_ThenTheOperationIsInvoked()
    {
        try
        {
            // Arrange
            ResetMotor();
            var page = await OpenPageAsync();

            // Act
            await GetButton(page, "Emergency Stop").ClickAsync();
            var dialog = page.GetByRole(AriaRole.Dialog);
            await Assertions.Expect(dialog).ToContainTextAsync("Confirm: Emergency Stop", new() { Timeout = Timeout });
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Execute", Exact = true }).ClickAsync();

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(
                () => GetMotor() is { IsPowered: false, CurrentSpeed: 0 },
                WaitTimeout,
                message: "The emergency stop was not invoked");
        }
        finally
        {
            ResetMotor();
        }
    }

    [Fact]
    public async Task WhenOperationButtonNeedsNoConfirmation_ThenTheOperationIsInvokedAtOnce()
    {
        try
        {
            // Arrange
            ResetMotor();
            GetMotor().IsPowered = false;
            var page = await OpenPageAsync();
            var powerOn = GetButton(page, "Power On");
            await Assertions.Expect(powerOn).ToBeEnabledAsync(new() { Timeout = Timeout });

            // Act
            await powerOn.ClickAsync();

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(() => GetMotor().IsPowered, WaitTimeout, message: "PowerOn was not invoked");
            await Assertions.Expect(page.GetByRole(AriaRole.Dialog)).ToHaveCountAsync(0);
            await Assertions.Expect(powerOn).ToBeDisabledAsync(new() { Timeout = Timeout });
        }
        finally
        {
            ResetMotor();
        }
    }

    [Fact]
    public async Task WhenOperationIsNotEnabled_ThenItsButtonIsDisabled()
    {
        // Arrange: PowerOn is enabled only while the motor is not powered.
        ResetMotor();

        // Act
        var page = await OpenPageAsync();

        // Assert
        await Assertions.Expect(GetButton(page, "Power On")).ToBeDisabledAsync(new() { Timeout = Timeout });
        await Assertions.Expect(GetButton(page, "Emergency Stop")).ToBeEnabledAsync(new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenOperationButtonInvokesAQuery_ThenItsResultIsShown()
    {
        // Arrange
        ResetMotor();
        var page = await OpenPageAsync();

        // Act
        await GetButton(page, "Get Diagnostics").ClickAsync();

        // Assert
        var dialog = page.GetByRole(AriaRole.Dialog);
        await Assertions.Expect(dialog).ToContainTextAsync("Get Diagnostics completed successfully.", new() { Timeout = Timeout });
        await Assertions.Expect(dialog).ToContainTextAsync("CurrentSpeed");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "OK", Exact = true }).ClickAsync();
        await Assertions.Expect(dialog).ToHaveCountAsync(0, new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenThePageIsInEditMode_ThenTheControlsAreDisabled()
    {
        // Arrange
        ResetMotor();
        var page = await OpenPageAsync();
        await Assertions.Expect(GetSwitch(page, "Power")).ToBeEnabledAsync(new() { Timeout = Timeout });

        // Act
        await page.SwitchToInlineModeAsync();

        // Assert: disabled for assistive technology as well, not only out of reach of the pointer.
        await Assertions.Expect(GetSwitch(page, "Power")).ToBeDisabledAsync(new() { Timeout = Timeout });
        await Assertions.Expect(GetSlider(page, "Target speed")).ToBeDisabledAsync(new() { Timeout = Timeout });
        await Assertions.Expect(GetSelect(page, "Motor mode").Locator("input").First).ToBeDisabledAsync(new() { Timeout = Timeout });
        await Assertions.Expect(GetButton(page, "Emergency Stop")).ToBeDisabledAsync(new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenControlTargetsAnEnumAndANumberWithoutRange_ThenItShowsASelectAndAValueRow()
    {
        // Act
        var page = await OpenPageAsync();

        // Assert
        await Assertions.Expect(GetSelect(page, "Control mode")).ToBeVisibleAsync(new() { Timeout = Timeout });
        await Assertions.Expect(page.GetByTestId("control-value").Filter(new() { HasText = "Control speed" }))
            .ToBeVisibleAsync(new() { Timeout = Timeout });
    }

    [Theory]
    [InlineData("Power", "Path", new[] { "IsPowered" }, new[] { "CurrentSpeed" }, new[] { "EmergencyStop" })]
    [InlineData("Power by operation", "On", new[] { "PowerOn", "EmergencyStop" }, new[] { "IsPowered" }, new string[] { })]
    [InlineData("Target speed", "Path", new[] { "Load", "TargetSpeed" }, new[] { "SpeedDelta", "IsPowered" }, new[] { "EmergencyStop" })]
    [InlineData("Motor mode", "Path", new[] { "Mode" }, new[] { "IsPowered", "Load" }, new[] { "GetDiagnostics" })]
    [InlineData("ctlmotor/EmergencyStop", "Path", new[] { "EmergencyStop", "GetDiagnostics" }, new[] { "IsPowered" }, new string[] { })]
    [InlineData("Control mode", "Path", new[] { "IsPowered", "Mode", "EmergencyStop" }, new string[] { }, new string[] { })]
    public async Task WhenThePathPickerOfAControlWidgetIsOpened_ThenOnlyTheTargetsItCanUseCanBeSelected(
        string widgetName, string argument, string[] selectable, string[] notSelectable, string[] notListed)
    {
        // Arrange: a widget is named by its label, else by its path.
        var page = await OpenPageAsync();
        await page.SwitchToInlineModeAsync();
        var actionBar = await page.Locator($".subject-component-wrapper.selectable[aria-label='{widgetName}']").SelectWidgetAsync();
        await actionBar.GetByTestId("edit-subject-button").ClickAsync();
        await page.GetByRole(AriaRole.Dialog)
            .GetByRole(AriaRole.Button, new() { Name = $"Browse {argument}", Exact = true })
            .ClickAsync();
        var picker = page.GetByTestId("subject-path-picker");

        // Act: a typed path expands the tree down to the properties of the motor.
        await picker.Locator("input").First.FillAsync($"/Demo/{PageName}/{MotorName}/{selectable[0]}");

        // Assert
        foreach (var name in selectable)
        {
            var item = GetPickerItem(picker, name);
            await Assertions.Expect(item).ToBeVisibleAsync(new() { Timeout = Timeout });
            await Assertions.Expect(item).Not.ToHaveClassAsync(NotSelectable);
        }

        foreach (var name in notSelectable)
        {
            var item = GetPickerItem(picker, name);
            await Assertions.Expect(item).ToBeVisibleAsync(new() { Timeout = Timeout });
            await Assertions.Expect(item).ToHaveClassAsync(NotSelectable);
        }

        // The tree is shown by now, and an operation is listed only where it can be selected.
        foreach (var name in notListed)
        {
            await Assertions.Expect(GetPickerItem(picker, name)).ToHaveCountAsync(0);
        }
    }
}
