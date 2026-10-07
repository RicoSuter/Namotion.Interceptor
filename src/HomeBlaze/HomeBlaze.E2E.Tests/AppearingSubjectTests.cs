using HomeBlaze.E2E.Tests.Infrastructure;
using HomeBlaze.Samples;
using HomeBlaze.Services;
using HomeBlaze.Storage.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Namotion.Interceptor;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for views opened on a path whose subject does not exist yet.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class AppearingSubjectTests
{
    private const int PageLoadTimeout = 30000;

    private readonly PlaywrightFixture _fixture;

    public AppearingSubjectTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task WhenSubjectAtPagePathIsAdded_ThenPageShowsItWithoutNavigation()
    {
        // Arrange
        // A space and an escape sequence in the key, which must be unescaped exactly once.
        var folderName = $"E2E Appearing %41 Page {Guid.NewGuid():N}";
        var root = await GetRootAsync();
        var motors = new List<Motor>();
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}pages/{Uri.EscapeDataString(folderName)}/Motor");
        await Assertions.Expect(page.GetByText("Subject not found at path"))
            .ToBeVisibleAsync(new() { Timeout = PageLoadTimeout });

        try
        {
            // Act - the folder appears before the subject in it
            motors.Add(await AddMotorAsync(root, $"{folderName}/Other.json", "E2E Other Motor"));
            motors.Add(await AddMotorAsync(root, $"{folderName}/Motor.json", "E2E Appearing Motor"));

            // Assert
            await Assertions.Expect(page).ToHaveTitleAsync("E2E Appearing Motor - HomeBlaze", new() { Timeout = PageLoadTimeout });
            await Assertions.Expect(page.GetByText("Subject not found at path")).Not.ToBeVisibleAsync();
            Assert.EndsWith($"/pages/{Uri.EscapeDataString(folderName)}/Motor", page.Url);
        }
        finally
        {
            await RemoveAsync(root, folderName, motors);
        }
    }

    [Fact]
    public async Task WhenSubjectsOnBrowserDeepLinkAreAdded_ThenPanesExtendToTheRequestedSubject()
    {
        // Arrange
        // A space and an escape sequence in the key, which must be unescaped exactly once.
        var folderName = $"E2E Appearing %41 Browser {Guid.NewGuid():N}";
        var root = await GetRootAsync();
        var motors = new List<Motor>();
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}browser/{Uri.EscapeDataString(folderName)}/Motor");

        var paneTitles = page.Locator("#scrollContainer h1");
        await Assertions.Expect(paneTitles).ToHaveCountAsync(1, new() { Timeout = PageLoadTimeout });

        try
        {
            // Act
            motors.Add(await AddMotorAsync(root, $"{folderName}/Other.json", "E2E Other Motor"));
            await Assertions.Expect(paneTitles).ToHaveCountAsync(2, new() { Timeout = PageLoadTimeout });
            await Assertions.Expect(paneTitles.Last).ToHaveTextAsync(folderName);

            motors.Add(await AddMotorAsync(root, $"{folderName}/Motor.json", "E2E Appearing Motor"));

            // Assert
            await Assertions.Expect(paneTitles).ToHaveCountAsync(3, new() { Timeout = PageLoadTimeout });
            await Assertions.Expect(paneTitles.Last).ToHaveTextAsync("E2E Appearing Motor");
            Assert.EndsWith($"/browser/{Uri.EscapeDataString(folderName)}/Motor", page.Url);
        }
        finally
        {
            await RemoveAsync(root, folderName, motors);
        }
    }

    private async Task<IInterceptorSubject> GetRootAsync()
    {
        return await _fixture.ServerServices.GetRequiredService<RootManager>().RootLoaded;
    }

    private static async Task<Motor> AddMotorAsync(IInterceptorSubject root, string path, string name)
    {
        var motor = new Motor(root.Context) { Name = name };
        await ((IStorageContainer)root).AddSubjectAsync(path, motor, CancellationToken.None);
        return motor;
    }

    private static async Task RemoveAsync(IInterceptorSubject root, string folderName, List<Motor> motors)
    {
        try
        {
            foreach (var motor in motors)
            {
                await ((IStorageContainer)root).DeleteSubjectAsync(motor, CancellationToken.None);
            }
        }
        finally
        {
            // The test data is reset by copying the source files over, so a folder a test adds stays on disk.
            var directory = Path.Combine(AppContext.BaseDirectory, "TestData", folderName);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
