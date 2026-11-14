using HomeBlaze.E2E.Tests.Infrastructure;
using HomeBlaze.Services;
using HomeBlaze.Virtual;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Namotion.Interceptor.Testing;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for the editor of a virtual subject: a subject created in the create dialog is stored and computes its
/// properties from the graph.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class VirtualSubjectEditorTests
{
    private const int Timeout = 30000;

    private readonly PlaywrightFixture _fixture;

    public VirtualSubjectEditorTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task WhenAVirtualSubjectIsCreatedWithAnExpression_ThenItsPropertyComputesFromTheGraph()
    {
        // Arrange
        await using var motor = await _fixture.AddStoredMotorAsync();
        motor.Subject.TargetSpeed = 1500;
        var name = $"Virtual{Guid.NewGuid():N}";
        var expression = $"/{motor.Name}/TargetSpeed * 2";
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}browser");
        await page.WaitUntilInteractiveAsync();
        try
        {
            // Act
            await page.Locator("#scrollContainer button:has-text('Create')").First.ClickAsync();
            await SubjectSetupDialogTests.PickTypeAsync(page, "virtualsubject");
            await page.GetByTestId("create-name-input").FillAsync(name);
            var dialog = SubjectSetupDialogTests.Dialog(page);
            await Assertions.Expect(dialog.GetByTestId("virtual-editor")).ToBeVisibleAsync(new() { Timeout = Timeout });
            await dialog.GetByTestId("virtual-add-property").ClickAsync();

            // The added entry is selected, so its form shows the suggested name once the click has been handled.
            var nameInput = dialog.GetByTestId("virtual-name");
            await Assertions.Expect(nameInput).ToHaveValueAsync("Property", new() { Timeout = Timeout });
            await nameInput.FillAsync("Double");
            await dialog.GetByTestId("virtual-expression-text").FillAsync(expression);
            await Assertions.Expect(SubjectSetupDialogTests.CreateButton(page)).ToBeEnabledAsync(new() { Timeout = Timeout });
            await SubjectSetupDialogTests.CreateButton(page).ClickAsync();
            await Assertions.Expect(dialog).Not.ToBeVisibleAsync(new() { Timeout = Timeout });

            // Assert
            var filePath = _fixture.GetPagePath($"{name}.json");
            await AsyncTestHelpers.WaitUntilAsync(() => File.Exists(filePath), timeout: TimeSpan.FromSeconds(30), message: "The virtual subject file was not written");
            Assert.Contains($"\"expression\": \"{expression}\"", await _fixture.ReadFileAsync(filePath));

            var resolver = _fixture.ServerServices.GetRequiredService<SubjectPathResolver>();
            var root = _fixture.ServerServices.GetRequiredService<RootManager>().Root!;
            await AsyncTestHelpers.WaitUntilAsync(
                () => VirtualExpression.ResolveProperty(resolver, $"/{name}/Double", root)?.GetValue() is decimal value && value == motor.Subject.TargetSpeed * 2m,
                timeout: TimeSpan.FromSeconds(30),
                message: "The virtual property did not compute from the motor");
        }
        finally
        {
            await _fixture.DeleteStoredSubjectByNameAsync(name);
        }
    }
}
