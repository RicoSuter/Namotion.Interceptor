using HomeBlaze.History.Abstractions;
using HomeBlaze.History.InMemory;
using HomeBlaze.History.Sqlite;
using Xunit;

namespace HomeBlaze.History.Parity.Tests;

public class HeldValueAcrossStoresTests
{
    private const string OutcomePath = "/Rules[evening]/LastRunOutcome";
    private const string CountPath = "/Rules[evening]/RunCount";

    [Fact]
    public async Task WhenTheLiveStoreCoversTheInstantWithoutAChangeAndThePersistentStoreHasNotFlushedIt_ThenTheHeldValueIsStillFound()
    {
        // Arrange - both engines with the defaults of their subjects: in memory for 60 s at priority 100, SQLite at
        // priority 50. A rule ran in the morning and again in the evening with the same outcome, so the evening run
        // recorded only its count, and SQLite last flushed 3 s before it.
        var start = new DateTimeOffset(2026, 11, 10, 6, 0, 0, TimeSpan.Zero);
        var now = start;
        var directory = Path.Combine(Path.GetTempPath(), "hb-held-" + Guid.NewGuid().ToString("N"));
        var live = new InMemoryHistoryStore(100, 1000, TimeSpan.FromSeconds(60), 8192, () => now);
        var persistent = new SqliteHistoryStore(50, directory, PartitionInterval.Weekly, TimeSpan.FromDays(30), 8192, () => now);
        try
        {
            void Record(string path, DateTimeOffset at, object? value, Type type)
            {
                live.Record(path, at, value, type);
                persistent.Record(path, at, value, type);
            }

            var morningRun = start.AddHours(2.5);
            Record(OutcomePath, morningRun, "Succeeded", typeof(string));
            Record(CountPath, morningRun.AddMilliseconds(1), 1L, typeof(long?));
            now = morningRun.AddSeconds(5);
            await persistent.FlushAsync(CancellationToken.None);

            var eveningRun = start.AddHours(15.5);
            now = eveningRun.AddSeconds(-3);
            await persistent.FlushAsync(CancellationToken.None);
            Record(CountPath, eveningRun, 2L, typeof(long?));
            now = eveningRun.AddSeconds(5);

            // Act
            var outcome = await new IHistoryStore[] { live, persistent }
                .GetValueAtAsync(OutcomePath, eveningRun, CancellationToken.None);

            // Assert
            Assert.NotNull(outcome);
            Assert.Equal(morningRun, outcome.Timestamp);
            Assert.Equal("Succeeded", outcome.Json?.GetString());
        }
        finally
        {
            persistent.Dispose();
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
                // Nothing was written.
            }
        }
    }
}
