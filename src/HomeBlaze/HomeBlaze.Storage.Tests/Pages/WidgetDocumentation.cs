using System.ComponentModel;
using System.Reflection;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Components;
using HomeBlaze.History.Blazor;

namespace HomeBlaze.Storage.Tests.Pages;

/// <summary>
/// The pages of the widget reference in the documentation, as a storage that loads the documentation holds them.
/// </summary>
public static class WidgetDocumentation
{
    /// <summary>The folder of the widget reference, relative to <see cref="DocumentationFolderFixture.DocumentationFolder"/>.</summary>
    public const string Folder = "administration/widgets";

    /// <summary>The only path of the widget pages that leads to nothing, in the sample of widget.md that shows the message for it.</summary>
    public const string PathToNothing = "/Missing/Motor";

    /// <summary>
    /// Gets the widget types that ship for markdown pages: the creatable types of the dashboard category and the history chart.
    /// </summary>
    public static IReadOnlyList<Type> GetShippedWidgetTypes() =>
        typeof(Card).Assembly.GetTypes()
            .Where(type => type.GetCustomAttribute<CategoryAttribute>()?.Category == "Dashboard"
                && type.GetCustomAttribute<ExcludeFromCreationAttribute>() is null)
            .Append(typeof(HistoryChart))
            .ToList();

    /// <summary>Gets the path of a page in the storage, for example <c>/administration/widgets/value-map.md</c>.</summary>
    public static string GetPagePath(string fileName) => $"/{Folder}/{fileName}";

    /// <summary>Gets the paths in the storage of every page of the widget reference, in ordinal order.</summary>
    public static TheoryData<string> GetPagePaths()
    {
        var folder = Path.Combine(RepositoryPagesTests.FindRepositoryRoot(), DocumentationFolderFixture.DocumentationFolder, Folder);
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.md").Order(StringComparer.Ordinal))
        {
            data.Add(GetPagePath(Path.GetFileName(file)));
        }

        return data;
    }
}
