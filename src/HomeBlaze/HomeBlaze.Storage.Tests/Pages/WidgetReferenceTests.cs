using System.Text.RegularExpressions;

namespace HomeBlaze.Storage.Tests.Pages;

/// <summary>
/// Checks that the widget reference describes every widget that ships, with a page of its own and a row in the overview.
/// </summary>
public partial class WidgetReferenceTests
{
    public static TheoryData<string> WidgetTypeNames()
    {
        var data = new TheoryData<string>();
        foreach (var type in WidgetDocumentation.GetShippedWidgetTypes().OrderBy(type => type.Name, StringComparer.Ordinal))
        {
            data.Add(type.FullName!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(WidgetTypeNames))]
    public void WhenWidgetShips_ThenItHasAPageAndARowInTheOverview(string fullName)
    {
        // Arrange
        var name = fullName[(fullName.LastIndexOf('.') + 1)..];
        var fileName = WordStart().Replace(name, "-$0").ToLowerInvariant() + ".md";
        var folder = Path.Combine(RepositoryPagesTests.FindRepositoryRoot(), DocumentationFolderFixture.DocumentationFolder, WidgetDocumentation.Folder);

        // Act
        var overview = File.ReadAllText(Path.Combine(folder, "overview.md"));

        // Assert
        Assert.True(File.Exists(Path.Combine(folder, fileName)), $"The widget reference has no page {fileName}.");
        Assert.Contains($"| [{name}]({fileName}) | `{fullName}` |", overview);
    }

    [GeneratedRegex("(?<!^)[A-Z]")]
    private static partial Regex WordStart();
}
