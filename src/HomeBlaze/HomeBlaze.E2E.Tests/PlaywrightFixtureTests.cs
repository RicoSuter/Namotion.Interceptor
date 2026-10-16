using HomeBlaze.E2E.Tests.Infrastructure;
using HomeBlaze.Services;
using HomeBlaze.Storage;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// What the tests rely on from the fixture itself.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class PlaywrightFixtureTests
{
    private readonly PlaywrightFixture _fixture;

    public PlaywrightFixtureTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task WhenAnEarlierTestLeftAChangeOnDisk_ThenAPassOfTheStorageKeepsThePlainSubjectAddedAfterwards()
    {
        // Arrange: a file that the storage knows is deleted behind it, as a test does that cleans up on disk.
        var root = (FluentStorageContainer)_fixture.ServerServices.GetRequiredService<RootManager>().Root!;
        const string fileName = "LeftBehind.md";
        var filePath = Path.Combine(AppContext.BaseDirectory, "TestData", fileName);
        await File.WriteAllTextAsync(filePath, "# Left behind");
        await root.ReconcileAsync();
        var isFileKnown = root.Children.ContainsKey(fileName);
        File.Delete(filePath);

        var (name, subject) = await _fixture.AddPlainSubjectToRootAsync();
        try
        {
            // Act: the pass that an earlier test asked for runs while this test uses its subject.
            await root.ReconcileAsync();

            // Assert
            Assert.True(isFileKnown);
            Assert.False(root.Children.ContainsKey(fileName));
            Assert.Same(subject, root.Children.GetValueOrDefault(name));
        }
        finally
        {
            _fixture.RemoveSubjectFromRoot(name, subject);
        }
    }
}
