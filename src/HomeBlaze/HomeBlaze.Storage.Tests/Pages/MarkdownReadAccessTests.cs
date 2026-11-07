using System.Reactive.Concurrency;
using System.Reactive.Linq;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Authorization.Abstractions;
using HomeBlaze.Components;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Internal;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace HomeBlaze.Storage.Tests.Pages;

public class MarkdownReadAccessTests
{
    private static readonly string Namespace = typeof(TestMeter).Namespace!;

    private static async Task<(MarkdownPageFixture Fixture, MarkdownFile Page)> AddPageAsync(string text)
    {
        var fixture = await MarkdownPageFixture.CreateWithWidgetsAndSamplesAsync();
        var page = await fixture.AddPageAsync("page.md", $$$"""
            :: {{{Namespace}}}.TestMeter#meter Power=1234.5 IsOn=true
            ::

            :: HomeBlaze.Components.ValueMap#busy
            ::: Case When="subject/IsOn and value > 1000" Text="busy at {{ value }}"
            ::

            {{{text}}}
            """);

        return (fixture, page);
    }

    private static string GetLastLine(string? content) => content!.TrimEnd().Split('\n')[^1];

    private static MethodMetadata GetReadEvaluatedContent(MarkdownFile page) =>
        (MethodMetadata)page.TryGetRegisteredSubject()!.TryGetProperty(nameof(MarkdownFile.ReadEvaluatedContent))!.GetValue()!;

    [Fact]
    public async Task WhenTheReaderMayReadEveryProperty_ThenTheExpressionIsRendered()
    {
        // Arrange
        var (_, page) = await AddPageAsync("Power: {{ meter/Power }}");

        // Act
        var evaluated = page.ReadEvaluatedContent(UnrestrictedPermissions.Instance);

        // Assert
        Assert.Equal("Power: 1.23 kW", GetLastLine(evaluated));
    }

    [Fact]
    public async Task WhenThePageIsReadAsAMethodWithoutPermissions_ThenItsExpressionsRenderEmpty()
    {
        // Arrange
        var (_, page) = await AddPageAsync("Power: [{{ meter/Power }}]");
        var method = GetReadEvaluatedContent(page);

        // Act
        var evaluated = (string?)await method.InvokeAsync([null], serviceProvider: null, CancellationToken.None);

        // Assert
        Assert.Equal("Power: []", GetLastLine(evaluated));
    }

    [Fact]
    public async Task WhenThePageIsReadAsAMethod_ThenThePermissionsOfTheServicesDecide()
    {
        // Arrange
        var (_, page) = await AddPageAsync("Power: [{{ meter/Power }}], on: [{{ meter/IsOn }}]");
        var method = GetReadEvaluatedContent(page);
        using var services = new ServiceCollection()
            .AddSingleton<IUserPermissions>(new ReadablePermissions(property => property.Name != nameof(TestMeter.Power)))
            .BuildServiceProvider();

        // Act
        var evaluated = (string?)await method.InvokeAsync([null], services, CancellationToken.None);

        // Assert
        Assert.Equal("Power: [], on: [Yes]", GetLastLine(evaluated));
    }

    [Fact]
    public async Task WhenTheReaderMayNotReadTheProperty_ThenTheExpressionRendersEmpty()
    {
        // Arrange
        var (_, page) = await AddPageAsync("Power: {{ meter/Power }}, on: {{ meter/IsOn }}");
        var permissions = new ReadablePermissions(property => property.Name != nameof(TestMeter.Power));

        // Act
        var evaluated = page.ReadEvaluatedContent(permissions);

        // Assert
        Assert.Equal("Power: , on: Yes", GetLastLine(evaluated));
    }

    [Fact]
    public async Task WhenTheReaderMayNotReadTheProperty_ThenItsSourcePathIsLeftOut()
    {
        // Arrange
        var (_, page) = await AddPageAsync("Power: {{ meter/Power }}, on: {{ meter/IsOn }}");
        var permissions = new ReadablePermissions(property => property.Name != nameof(TestMeter.Power));

        // Act
        var evaluated = page.ReadEvaluatedContent(permissions, includeSourcePaths: true);
        var evaluatedUnrestricted = page.ReadEvaluatedContent(UnrestrictedPermissions.Instance, includeSourcePaths: true);

        // Assert
        Assert.Equal("Power: , on: Yes (/page.md/meter/IsOn)", GetLastLine(evaluated));
        Assert.Equal("Power: 1.23 kW (/page.md/meter/Power), on: Yes (/page.md/meter/IsOn)", GetLastLine(evaluatedUnrestricted));
    }

    [Fact]
    public async Task WhenTheReaderMayNotReadAPropertyThatAValueMapReads_ThenTheCaseDoesNotSeeIt()
    {
        // Arrange
        var (_, page) = await AddPageAsync("State: {{ meter/Power | map:busy }}");
        var permissions = new ReadablePermissions(property => property.Name != nameof(TestMeter.IsOn));

        // Act
        var evaluated = page.ReadEvaluatedContent(permissions);
        var evaluatedUnrestricted = page.ReadEvaluatedContent(UnrestrictedPermissions.Instance);

        // Assert
        Assert.Equal("State: 1.23 kW", GetLastLine(evaluated));
        Assert.Equal("State: busy at 1.23 kW", GetLastLine(evaluatedUnrestricted));
    }

    [Fact]
    public async Task WhenTheReaderMayReadNoMemberOfAValueMap_ThenTheMapReadsAsMissing()
    {
        // Arrange
        var (_, page) = await AddPageAsync("State: {{ meter/Power | map:busy }}, other: {{ meter/Power | map:missing }}");
        var map = page.Children.Values.OfType<ValueMap>().Single();
        var permissions = new ReadablePermissions(property => !ReferenceEquals(property.Subject, map));

        // Act
        var evaluated = page.ReadEvaluatedContent(permissions);
        var evaluatedUnrestricted = page.ReadEvaluatedContent(UnrestrictedPermissions.Instance);

        // Assert
        Assert.Equal("State: Cannot resolve map: busy, other: Cannot resolve map: missing", GetLastLine(evaluated));
        Assert.Equal("State: busy at 1.23 kW, other: Cannot resolve map: missing", GetLastLine(evaluatedUnrestricted));
    }

    [Fact]
    public async Task WhenTheViewerMayNotReadTheProperty_ThenTheShownTextIsEmptyWhileTheSharedValueStillFollowsIt()
    {
        // Arrange
        var (_, page) = await AddPageAsync("{{ meter/Power }}");
        var expression = page.Children.Values.OfType<RenderExpression>().Single();
        var meter = page.Children.Values.OfType<TestMeter>().Single();
        var values = new List<object?>();
        using var subscription = ((IInterceptorSubject)page).Context.GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Where(change => ReferenceEquals(change.Property.Subject, expression) &&
                             change.Property.Name == nameof(RenderExpression.Value))
            .Subscribe(change => values.Add(change.GetNewValue<object?>()));

        // Act
        var text = expression.GetText(null, DenyAllPermissions.Instance);
        meter.Power = 10m;

        // Assert
        Assert.Equal(string.Empty, text);
        Assert.Equal([10m], values);
    }

    // May do everything except read the properties the predicate refuses.
    private sealed class ReadablePermissions(Func<RegisteredSubjectProperty, bool> canRead) : IUserPermissions
    {
        public event Action? Changed
        {
            add { }
            remove { }
        }

        public bool CanRead(RegisteredSubjectProperty member) => canRead(member);

        public bool CanWrite(RegisteredSubjectProperty member) => true;

        public bool CanInvoke(RegisteredSubjectProperty member) => true;

        public bool CanAdminister(RegisteredSubjectProperty member) => true;

        public bool CanReadHistory(RegisteredSubjectProperty member) => true;

        public bool CanShow(RegisteredSubject subject) => true;

        public bool HoldsAllRoles(IReadOnlyCollection<string> roles) => true;

        public bool CanActThrough(IReadOnlyCollection<string> roles) => true;

        public IReadOnlyList<string> GetRolesWithDenyRulesMissingFrom(IReadOnlyCollection<string> roles) => [];
    }
}
