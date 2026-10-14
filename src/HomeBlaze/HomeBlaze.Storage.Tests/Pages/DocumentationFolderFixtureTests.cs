using HomeBlaze.Abstractions;
using HomeBlaze.Samples;
using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;

namespace HomeBlaze.Storage.Tests.Pages;

[Collection(MarkdownPageFixture.StorageCollection)]
public class DocumentationFolderFixtureTests
{
    [Fact]
    public async Task WhenWidgetsFolderIsLoaded_ThenItsJsonFilesAreTheTwoSampleMotors()
    {
        // Act
        using var fixture = await DocumentationFolderFixture.LoadAsync("administration/widgets");

        // Assert
        var pump = Assert.IsType<Motor>(fixture.FindSubject("/administration/widgets/Pump"));
        var fan = Assert.IsType<Motor>(fixture.FindSubject("/administration/widgets/Fan"));
        Assert.Equal(("Pump", 1200), (pump.Name, pump.TargetSpeed));
        Assert.Equal(("Fan", 800), (fan.Name, fan.TargetSpeed));
    }

    [Fact]
    public async Task WhenFolderIsLoaded_ThenEveryPageIsParsedAndReachesAFileOfASubfolderByAPathRelativeToItsFolder()
    {
        // Arrange
        using var fixture = await DocumentationFolderFixture.LoadAsync("administration");
        var resolver = fixture.Services.GetRequiredService<SubjectPathResolver>();
        var page = fixture.FindPage("/administration/paths.md");

        // Act
        var pump = resolver.ResolveScopedSubject("../widgets/Pump", PathStyle.Canonical, page);

        // Assert
        Assert.Same(fixture.FindSubject("/administration/widgets/Pump"), pump);
        Assert.Contains(fixture.Pages, loaded => loaded.FullPath == "/administration/page-widgets.md");
        Assert.All(fixture.Pages, loaded => Assert.NotEmpty(loaded.Children));
    }
}
