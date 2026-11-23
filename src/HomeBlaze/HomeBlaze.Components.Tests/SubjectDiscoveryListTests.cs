using HomeBlaze.Abstractions.Discovery;
using HomeBlaze.Components.Dialogs;
using HomeBlaze.Components.Tests.Models;
using HomeBlaze.Services;
using HomeBlaze.Services.Discovery;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor;
using Xunit;

namespace HomeBlaze.Components.Tests;

public class SubjectDiscoveryListTests
{
    private static readonly SubjectTypeDescriptor Hue = new(typeof(DiscoveryListHubSubject), "Hue Bridge", null, "Lighting", "Hub");
    private static readonly SubjectTypeDescriptor Shelly = new(typeof(DiscoveryListSwitchSubject), "Shelly device", null, "Switches", "Power");

    private static DiscoveredSubject Found(
        SubjectTypeDescriptor descriptor, string name, IInterceptorSubject? existingSubject = null, Func<IInterceptorSubject, bool>? isMatch = null) =>
        new(descriptor.Type, new FoundDevice(name) { IsMatch = isMatch }, existingSubject);

    private static IEnumerable<string> Names(SubjectDiscoveryList list) =>
        list.Rows.Select(row => row.Discovered.Result.DisplayName);

    [Fact]
    public void WhenResultsArriveInAnyOrder_ThenRowsAreOrderedByTypeAndThenName()
    {
        // Arrange
        var list = new SubjectDiscoveryList();

        // Act
        list.Add(Found(Shelly, "shellyplus1pm-b"), Shelly);
        list.Add(Found(Hue, "Zeta"), Hue);
        list.Add(Found(Shelly, "shellyplus1pm-a"), Shelly);
        list.Add(Found(Hue, "Hue Bridge"), Hue);

        // Assert
        Assert.Equal(["Hue Bridge", "Zeta", "shellyplus1pm-a", "shellyplus1pm-b"], Names(list));
    }

    [Fact]
    public void WhenSomeDevicesAreAlreadyAdded_ThenTheyAreListedAfterTheOthers()
    {
        // Arrange
        var list = new SubjectDiscoveryList();
        var existingSubject = new DiscoveryListHubSubject();

        // Act
        list.Add(Found(Hue, "Alpha", existingSubject), Hue);
        list.Add(Found(Shelly, "Delta"), Shelly);
        list.Add(Found(Shelly, "Beta", existingSubject), Shelly);
        list.Add(Found(Hue, "Gamma"), Hue);

        // Assert
        Assert.Equal(["Gamma", "Delta", "Alpha", "Beta"], Names(list));
    }

    [Fact]
    public void WhenARowIsMarkedAdded_ThenItHasTheSubjectAndMovesAfterTheRowsNotAdded()
    {
        // Arrange
        var list = new SubjectDiscoveryList();
        var existingSubject = new DiscoveryListHubSubject();
        var createdSubject = new DiscoveryListHubSubject();
        list.Add(Found(Hue, "Alpha"), Hue);
        list.Add(Found(Shelly, "Beta"), Shelly);
        list.Add(Found(Shelly, "Zeta", existingSubject), Shelly);
        list.Add(Found(Hue, "Gamma"), Hue);

        // Act
        list.MarkAdded(Found(Hue, "Alpha"), createdSubject, NullLogger.Instance);

        // Assert
        Assert.Equal(["Gamma", "Beta", "Alpha", "Zeta"], Names(list));
        Assert.Same(createdSubject, list.Rows[2].Discovered.ExistingSubject);
    }

    [Fact]
    public void WhenNoRowHasTheTypeAndKeyAndNoneMatches_ThenMarkAddedChangesNothing()
    {
        // Arrange
        var list = new SubjectDiscoveryList();
        list.Add(Found(Hue, "Alpha"), Hue);

        // Act
        list.MarkAdded(Found(Shelly, "Alpha"), new DiscoveryListSwitchSubject(), NullLogger.Instance);

        // Assert
        Assert.Null(Assert.Single(list.Rows).Discovered.ExistingSubject);
    }

    [Fact]
    public void WhenTheCreatedSubjectMatchesOtherRows_ThenTheyAreMarkedAddedAsWell()
    {
        // Arrange
        var list = new SubjectDiscoveryList();
        var createdSubject = new DiscoveryListHubSubject();
        var askedSubjects = new List<IInterceptorSubject>();
        bool MatchesCreated(IInterceptorSubject subject)
        {
            askedSubjects.Add(subject);
            return true;
        }

        list.Add(Found(Hue, "Alpha"), Hue);
        list.Add(Found(Hue, "Alpha 2", isMatch: MatchesCreated), Hue);
        list.Add(Found(Hue, "Beta", isMatch: _ => false), Hue);
        list.Add(Found(Hue, "Gamma", isMatch: MatchesCreated), Hue);

        // Act
        list.MarkAdded(Found(Hue, "Alpha"), createdSubject, NullLogger.Instance);

        // Assert
        Assert.Equal(["Beta", "Alpha", "Alpha 2", "Gamma"], Names(list));
        Assert.Null(list.Rows[0].Discovered.ExistingSubject);
        Assert.All(list.Rows.Skip(1), row => Assert.Same(createdSubject, row.Discovered.ExistingSubject));
        Assert.Equal([createdSubject, createdSubject], askedSubjects);
    }

    [Fact]
    public void WhenARowHasAnotherSubjectType_ThenItIsNotMatchedAgainstTheCreatedSubject()
    {
        // Arrange
        var list = new SubjectDiscoveryList();
        var createdSubject = new DiscoveryListHubSubject();
        var isAsked = false;
        list.Add(Found(Hue, "Alpha"), Hue);
        list.Add(Found(Shelly, "Delta", isMatch: _ => isAsked = true), Shelly);

        // Act
        list.MarkAdded(Found(Hue, "Alpha"), createdSubject, NullLogger.Instance);

        // Assert
        Assert.False(isAsked);
        Assert.Null(list.Rows.Single(row => row.Discovered.Result.DisplayName == "Delta").Discovered.ExistingSubject);
    }

    [Fact]
    public void WhenTheCreatedSubjectDoesNotMatchThePickedRow_ThenThePickedRowIsMarkedAddedAnyway()
    {
        // Arrange
        var list = new SubjectDiscoveryList();
        var createdSubject = new DiscoveryListHubSubject();
        list.Add(Found(Hue, "Alpha", isMatch: _ => false), Hue);
        list.Add(Found(Hue, "Beta"), Hue);

        // Act
        list.MarkAdded(Found(Hue, "Alpha"), createdSubject, NullLogger.Instance);

        // Assert
        Assert.Equal(["Beta", "Alpha"], Names(list));
        Assert.Same(createdSubject, list.Rows[1].Discovered.ExistingSubject);
    }

    [Fact]
    public void WhenARowIsAlreadyAdded_ThenItKeepsItsSubjectAndIsNotMatched()
    {
        // Arrange
        var list = new SubjectDiscoveryList();
        var existingSubject = new DiscoveryListHubSubject();
        var createdSubject = new DiscoveryListHubSubject();
        var isAsked = false;
        list.Add(Found(Hue, "Alpha"), Hue);
        list.Add(Found(Hue, "Beta", existingSubject, _ => isAsked = true), Hue);

        // Act
        list.MarkAdded(Found(Hue, "Alpha"), createdSubject, NullLogger.Instance);

        // Assert
        Assert.False(isAsked);
        Assert.Same(existingSubject, list.Rows.Single(row => row.Discovered.Result.DisplayName == "Beta").Discovered.ExistingSubject);
    }

    [Fact]
    public void WhenMatchesThrows_ThenItIsLoggedAndTheRowIsNotMarkedAdded()
    {
        // Arrange
        var list = new SubjectDiscoveryList();
        var createdSubject = new DiscoveryListHubSubject();
        var logger = new RecordingLogger();
        list.Add(Found(Hue, "Alpha"), Hue);
        list.Add(Found(Hue, "Beta", isMatch: _ => throw new InvalidOperationException("The match failed.")), Hue);
        list.Add(Found(Hue, "Gamma", isMatch: _ => true), Hue);

        // Act
        list.MarkAdded(Found(Hue, "Alpha"), createdSubject, logger);

        // Assert
        Assert.Equal(["Beta", "Alpha", "Gamma"], Names(list));
        Assert.Null(list.Rows[0].Discovered.ExistingSubject);
        Assert.Same(createdSubject, list.Rows[2].Discovered.ExistingSubject);
        Assert.Equal([LogLevel.Debug], logger.Levels);
    }

    [Fact]
    public void WhenNamesDifferOnlyInCaseOrKey_ThenTheOrderIsTheSameForAnyArrivalOrder()
    {
        // Arrange
        var upperCase = Found(Hue, "BRIDGE");
        var lowerCase = Found(Hue, "bridge");
        var firstKey = new DiscoveredSubject(Hue.Type, new FoundDevice("Same", "a"), null);
        var secondKey = new DiscoveredSubject(Hue.Type, new FoundDevice("Same", "b"), null);
        var first = new SubjectDiscoveryList();
        var second = new SubjectDiscoveryList();

        // Act
        foreach (var discovered in new[] { upperCase, lowerCase, secondKey, firstKey })
        {
            first.Add(discovered, Hue);
        }

        foreach (var discovered in new[] { firstKey, secondKey, lowerCase, upperCase })
        {
            second.Add(discovered, Hue);
        }

        // Assert
        Assert.Equal(first.Rows.Select(row => row.Discovered.Result.Key), second.Rows.Select(row => row.Discovered.Result.Key));
        Assert.Equal(["a", "b"], first.Rows.Select(row => row.Discovered.Result.Key).Where(key => key is "a" or "b"));
        Assert.Equal(["BRIDGE", "bridge"], first.Rows.Select(row => row.Discovered.Result.Key).Where(key => key.Equals("bridge", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void WhenADiscoveryFails_ThenItsTypeIsNamedInAFailureLine()
    {
        // Arrange
        var list = new SubjectDiscoveryList();

        // Act
        list.AddFailure(Hue);

        // Assert
        Assert.Equal(["Hue Bridge: search failed"], list.Failures);
    }

    [Fact]
    public void WhenTheListIsCleared_ThenRowsAndFailuresAreGone()
    {
        // Arrange
        var list = new SubjectDiscoveryList();
        list.Add(Found(Hue, "Bridge"), Hue);
        list.AddFailure(Shelly);

        // Act
        list.Clear();

        // Assert
        Assert.Empty(list.Rows);
        Assert.Empty(list.Failures);
    }

    private sealed record FoundDevice(string DisplayName, string? KeyOverride = null) : IDiscoveryResult
    {
        public Func<IInterceptorSubject, bool>? IsMatch { get; init; }

        public string Key => KeyOverride ?? DisplayName;

        public string? Detail => null;

        public bool Matches(IInterceptorSubject subject) => IsMatch?.Invoke(subject) ?? false;
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Levels.Add(logLevel);
        }
    }
}
