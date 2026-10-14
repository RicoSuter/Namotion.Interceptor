using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Components;
using HomeBlaze.Services;
using HomeBlaze.Storage.Internal;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;

namespace HomeBlaze.Storage.Tests.Pages;

/// <summary>
/// Loads the widget reference with the folder around it, as the application loads it, so that the paths of its
/// samples that start at the folder of a page, such as <c>../Pump</c>, are followed as well. A sample alone cannot
/// say whether such a path is right, see <see cref="DocumentationSamplesTests"/>.
/// </summary>
[Collection(MarkdownPageFixture.StorageCollection)]
public class WidgetDocumentationPathTests
{
    private const string Folder = "administration";
    private const string WidgetsFolder = Folder + "/widgets";
    private const string PageWidgetsPage = "/" + Folder + "/page-widgets.md";

    // The sample of the Widget section that shows the message for a path that leads nowhere.
    private const string PathToNothing = "/Missing/Motor";

    public static TheoryData<string> Pages()
    {
        var folder = Path.Combine(RepositoryPagesTests.FindRepositoryRoot(), DocumentationFolderFixture.DocumentationFolder, WidgetsFolder);
        var data = new TheoryData<string> { PageWidgetsPage };
        foreach (var file in Directory.EnumerateFiles(folder, "*.md").Order(StringComparer.Ordinal))
        {
            data.Add($"/{WidgetsFolder}/{Path.GetFileName(file)}");
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task WhenPageIsLoadedWithItsFolder_ThenEveryExpressionLeadsToAProperty(string pagePath)
    {
        // Arrange
        using var fixture = await DocumentationFolderFixture.LoadAsync(Folder);
        var resolver = fixture.Services.GetRequiredService<SubjectPathResolver>();

        // Act
        var page = fixture.FindPage(pagePath);

        // Assert
        var pathsToNothing = DocumentationFolderFixture.FindSubjects(page)
            .OfType<RenderExpression>()
            .Where(expression => resolver.ResolveScopedProperty(expression.Path, PathStyle.Canonical, page) is null)
            .Select(expression => expression.Path);

        Assert.Empty(pathsToNothing);
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task WhenPageIsLoadedWithItsFolder_ThenEveryPathOfAWidgetLeadsToItsTarget(string pagePath)
    {
        // Arrange
        using var fixture = await DocumentationFolderFixture.LoadAsync(Folder);
        var resolver = fixture.Services.GetRequiredService<SubjectPathResolver>();

        // Act
        var page = fixture.FindPage(pagePath);

        // Assert
        var pathsToNothing = DocumentationFolderFixture.FindSubjects(page)
            .SelectMany(subject => FindPaths(subject)
                .Where(path => path.Text != PathToNothing && !LeadsToTarget(path, subject, resolver))
                .Select(path => $"{subject.GetType().Name}.{path.PropertyName}={path.Text}"));

        Assert.Empty(pathsToNothing);
    }

    /// <summary>
    /// Gets the paths that the subject holds: the path of a <see cref="Widget"/>, which leads to any subject, and
    /// every property marked with <see cref="SubjectPathAttribute"/>, which says where its path leads.
    /// </summary>
    private static IEnumerable<WidgetPath> FindPaths(IInterceptorSubject subject)
    {
        if (subject is Widget widget)
        {
            yield return new WidgetPath(nameof(Widget.Path), widget.Path, typeof(IInterceptorSubject));
            yield break;
        }

        foreach (var property in subject.TryGetRegisteredSubject()!.Properties)
        {
            if (property.ReflectionAttributes.OfType<SubjectPathAttribute>().FirstOrDefault() is { } attribute &&
                property.GetValue() is string { Length: > 0 } path)
            {
                yield return new WidgetPath(property.Name, path, attribute.SubjectType);
            }
        }
    }

    // The same lookups as the widgets do: a path without a subject type ends at a property.
    private static bool LeadsToTarget(WidgetPath path, IInterceptorSubject subject, SubjectPathResolver resolver) =>
        path.SubjectType is null
            ? resolver.ResolveScopedProperty(path.Text, PathStyle.Canonical, subject) is not null
            : path.SubjectType.IsInstanceOfType(resolver.ResolveScopedSubject(path.Text, PathStyle.Canonical, subject));

    /// <summary>A path that a widget holds. <see cref="SubjectType"/> is null for a path to a property.</summary>
    private readonly record struct WidgetPath(string PropertyName, string Text, Type? SubjectType);
}
