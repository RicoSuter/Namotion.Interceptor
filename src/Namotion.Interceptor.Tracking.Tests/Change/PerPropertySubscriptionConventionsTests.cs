using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Tracking.Tests.Change;

public class PerPropertySubscriptionConventionsTests
{
    // SubscribeToPath (PR #381) builds on per-property subscriptions; listed ahead of its arrival.
    private static readonly string[] SensitiveMarkers =
    [
        "PropertyChangeSubscriptions.",
        "PropertyChangeSubscription.Create",
        // Also matches SubscribeToPropertyInline as a substring.
        "SubscribeToProperty",
        "SubscribeToPath",
        "IPropertyChangeObserver",
        "PropertyChangeCallback",
        // The scheduled channel and the observable adapter install real per-property subscriptions
        // without naming any type above: the observable is subscribed in Rx form
        // (.Subscribe(change => ...)), which matches none of the lambda markers below. One marker
        // covers both the extension method and the type backing it, in any construction form,
        // and cannot collide with the unrelated context-level GetPropertyChangeObservable.
        "InlineChangeObservable",
        "ScheduledPropertySubscription",
        // The low-level PropertyReference.SubscribeInline overloads taking a callback name none
        // of the types above, so match the method itself and the scheduled lambda form (both `in`
        // spellings), since the scheduled overloads keep the plain Subscribe name.
        "SubscribeInline",
        ".Subscribe((in ",
        ".Subscribe(static (in ",
    ];

    [Fact]
    public void WhenTestFileUsesPerPropertySubscriptionState_ThenItJoinsTheSerializedCollection()
    {
        // The subscription count is process-wide and xUnit runs collections in parallel: a test
        // creating per-property subscriptions outside the serialized collection intermittently
        // breaks the collection's count and idle-gate assertions, and the collection's
        // ResetForTests zeroes the count under the rogue test's live subscription, losing its
        // deliveries. Files that mention the collection type (including this one) pass.
        var offenders = Directory
            .EnumerateFiles(GetTestProjectDirectory(), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Select(file => (Name: Path.GetFileName(file), Content: File.ReadAllText(file)))
            .Where(file => SensitiveMarkers.Any(file.Content.Contains))
            .Where(file => !file.Content.Contains(nameof(PerPropertySubscriptionCollection)))
            .Select(file => file.Name)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "These test files use per-property subscriptions or the process-wide subscription count " +
            "but do not declare [Collection(PerPropertySubscriptionCollection.Name)]: " +
            $"{string.Join(", ", offenders)}. See the comment on PerPropertySubscriptionCollection.");
    }

    private static string GetTestProjectDirectory([CallerFilePath] string thisFile = "")
        => Path.GetDirectoryName(Path.GetDirectoryName(thisFile))!;
}
