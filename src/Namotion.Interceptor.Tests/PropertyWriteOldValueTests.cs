using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Testing;

namespace Namotion.Interceptor.Tests;

public class PropertyWriteOldValueTests
{
    private const int WritesPerWriter = 200_000;

    /// <summary>
    /// Two writers store uniform wide values into one property. The old value a commit reports is the
    /// value the property held immediately before its store, so it is uniform (never torn) and equal to
    /// the new value of the commit with the preceding revision, whichever writer made it.
    /// </summary>
    [Fact]
    public async Task WhenWideStructIsWrittenByTwoWriters_ThenEveryCommittedOldValueIsUntornAndChainsToThePreviousCommit()
    {
        // Arrange
        var recorder = new CommitRecorder();
        var context = InterceptorSubjectContext
            .Create()
            .WithService(() => recorder, _ => false);

        var subject = new WideValueSubject(context);
        using var start = new ManualResetEventSlim(false);

        var positive = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() =>
        {
            start.Wait();
            for (var index = 1; index <= WritesPerWriter; index++)
            {
                subject.Value = WideValue.Filled(index);
            }
        }, "positive");

        var negative = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() =>
        {
            start.Wait();
            for (var index = 1; index <= WritesPerWriter; index++)
            {
                subject.Value = WideValue.Filled(-index);
            }
        }, "negative");

        // Act
        start.Set();
        await Task.WhenAll(positive, negative);

        // Assert
        var commits = recorder.Commits;
        Assert.Equal(2 * WritesPerWriter, commits.Count);
        commits.Sort((left, right) => left.Revision.CompareTo(right.Revision));

        var torn = commits.FindIndex(commit => !commit.OldIsUniform);
        Assert.True(torn < 0,
            $"Commit at revision {(torn < 0 ? 0 : commits[torn].Revision)} reported a torn old value: " +
            "the old value was read outside the subject lock.");

        for (var index = 1; index < commits.Count; index++)
        {
            var previous = commits[index - 1];
            var current = commits[index];
            Assert.True(current.OldFirstLane == previous.NewFirstLane,
                $"Commit at revision {current.Revision} reported old value {current.OldFirstLane}, " +
                $"but the preceding commit at revision {previous.Revision} stored {previous.NewFirstLane}: " +
                "the old value was not read under the subject lock immediately before the store.");
        }
    }

    // Records each commit after the terminal write from outside the lock, so the list order is
    // arbitrary and the assertions sort by revision. Only one lane is kept per value to keep the
    // recording small; the uniformity flag carries the torn-read evidence.
    private sealed class CommitRecorder : IWriteInterceptor
    {
        public List<(long Revision, long OldFirstLane, long NewFirstLane, bool OldIsUniform)> Commits { get; } = new(2 * WritesPerWriter);

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            next(ref context);

            if (context.CurrentValue is WideValue oldValue && context.NewValue is WideValue newValue)
            {
                lock (Commits)
                {
                    Commits.Add((context.Revision, oldValue.First.A, newValue.First.A, oldValue.IsUniform));
                }
            }
        }
    }
}
