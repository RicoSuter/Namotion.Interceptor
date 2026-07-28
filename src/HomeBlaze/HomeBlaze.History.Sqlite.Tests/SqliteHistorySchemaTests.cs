using HomeBlaze.History.Abstractions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace HomeBlaze.History.Sqlite.Tests;

/// <summary>
/// Guards the durable on-disk format. The behavioural suites go through the store's API and would keep
/// passing across a schema change that silently orphaned every file already written, so these assert on
/// the bytes: the stamps that identify a file, the checks that refuse one this build cannot read, and
/// the retention that applies to the database no partition sweep ever deletes.
/// </summary>
public sealed class SqliteHistorySchemaTests : IDisposable
{
    private static readonly DateTimeOffset Base = new(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "hb-sqlite-schema-" + Guid.NewGuid().ToString("N"));

    private SqliteHistoryStore NewCore(DateTimeOffset now, int maxAgeSeconds = 3600) =>
        new(priority: 50, databaseDirectory: _directory, PartitionInterval.Weekly,
            TimeSpan.FromSeconds(maxAgeSeconds), maxJsonSize: 8192, () => now);

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
        catch { /* best effort temp cleanup */ }
    }

    private string MetadataFile => Path.Combine(_directory, "metadata.db");

    private static SqliteConnection OpenDirectly(string file)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static long Scalar(string file, string sql)
    {
        using var connection = OpenDirectly(file);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    [Fact]
    public async Task WhenAPartitionIsCreated_ThenItCarriesTheFormatStamps()
    {
        // Arrange
        using (var core = NewCore(Base.AddSeconds(10)))
        {
            core.Record("/a/Value", Base, 1.5d, typeof(double));
            await core.FlushAsync(CancellationToken.None);
        }

        // Act
        var partition = Directory.EnumerateFiles(_directory, "*.db")
            .Single(file => Path.GetFileName(file) != "metadata.db");

        // Assert - application_id marks the family, user_version the revision, and page_size is fixed
        // at creation: a file written without them cannot be told apart from a foreign database later.
        Assert.Equal(0x48424831, Scalar(partition, "PRAGMA application_id;"));
        Assert.Equal(1, Scalar(partition, "PRAGMA user_version;"));
        Assert.Equal(2048, Scalar(partition, "PRAGMA page_size;"));
        Assert.Equal(0x48424831, Scalar(MetadataFile, "PRAGMA application_id;"));
        Assert.Equal(1, Scalar(MetadataFile, "PRAGMA user_version;"));
    }

    [Fact]
    public async Task WhenADatabaseIsNewerThanTheBuild_ThenItIsRefusedRatherThanPartlyRead()
    {
        // Arrange
        using (var core = NewCore(Base.AddSeconds(10)))
        {
            core.Record("/a/Value", Base, 1.5d, typeof(double));
            await core.FlushAsync(CancellationToken.None);
        }

        using (var connection = OpenDirectly(MetadataFile))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version=99;";
            command.ExecuteNonQuery();
        }

        // Act & Assert - without the check a newer file is read as far as the columns happen to line
        // up, so a shape change that only altered meaning would be applied to data it does not describe.
        var exception = Assert.Throws<InvalidOperationException>(() => NewCore(Base.AddSeconds(20)));
        Assert.Contains("99", exception.Message);
    }

    [Fact]
    public void WhenADatabasePredatesTheVersionStamp_ThenItIsRefusedWithAnActionableMessage()
    {
        // Arrange - a file with tables but no stamp was written before the format was versioned, so its
        // shape is whatever an older build happened to use.
        Directory.CreateDirectory(_directory);
        using (var connection = OpenDirectly(MetadataFile))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE moves (ts INTEGER NOT NULL);";
            command.ExecuteNonQuery();
        }

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() => NewCore(Base));
        Assert.Contains("Delete the history directory", exception.Message);
    }

    [Fact]
    public async Task WhenMovesAgeOut_ThenSweepKeepsOnlyTheNewestOneAtOrBeforeTheCutoff()
    {
        // Arrange - the metadata database is never deleted by retention, so without pruning its move
        // list grows for the lifetime of the installation while every query reads all of it.
        var now = Base.AddSeconds(10);
        using var core = NewCore(now);
        core.RecordMove(now.AddSeconds(-7200), "/one", "/two");
        core.RecordMove(now.AddSeconds(-5400), "/two", "/three");
        core.RecordMove(now.AddSeconds(-1800), "/three", "/four");
        await core.FlushAsync(CancellationToken.None);
        Assert.Equal(3, Scalar(MetadataFile, "SELECT COUNT(*) FROM moves;"));

        // Act - the cutoff is now minus the hour of retention, so the first two moves precede it.
        core.Sweep();

        // Assert - the newest move at or before the cutoff stays, because it bounds the leg covering
        // the cutoff instant; only the one fully superseded by it goes.
        Assert.Equal(2, Scalar(MetadataFile, "SELECT COUNT(*) FROM moves;"));
        Assert.Equal(
            EpochTicks.ToEpochTicks(now.AddSeconds(-5400)),
            Scalar(MetadataFile, "SELECT MIN(ts) FROM moves;"));
    }

    [Fact]
    public async Task WhenAPathIsRecordedRepeatedly_ThenItIsInternedOnceAndRowsReferenceIt()
    {
        // Arrange - the path is stored once per partition rather than on every row, which is what keeps
        // a partition file from being mostly repeated path text.
        using (var core = NewCore(Base.AddSeconds(60)))
        {
            for (var index = 0; index < 25; index++)
            {
                core.Record("/a/Value", Base.AddSeconds(index), 1.5d + index, typeof(double));
            }

            await core.FlushAsync(CancellationToken.None);
        }

        var partition = Directory.EnumerateFiles(_directory, "*.db")
            .Single(file => Path.GetFileName(file) != "metadata.db");

        // Act & Assert
        Assert.Equal(1, Scalar(partition, "SELECT COUNT(*) FROM paths;"));
        Assert.Equal(25, Scalar(partition, "SELECT COUNT(*) FROM history;"));
        Assert.Equal(
            25, Scalar(partition, "SELECT COUNT(*) FROM history h JOIN paths p ON p.id = h.path_id;"));

        // The view is the reason ad-hoc SQL stays readable once ids replaced the inline path.
        Assert.Equal(
            25, Scalar(partition, "SELECT COUNT(*) FROM history_paths WHERE path = '/a/Value';"));
    }

    [Fact]
    public async Task WhenAPropertyChangesType_ThenTheStoredColumnKindFollowsIt()
    {
        // Arrange - the interned row carries the column kind that path_meta used to, and a read routes
        // on it, so a type change has to update it rather than leave the first-seen kind in place.
        using var core = NewCore(Base.AddSeconds(60));
        core.Record("/a/Value", Base, 1L, typeof(long));
        await core.FlushAsync(CancellationToken.None);

        var partition = Directory.EnumerateFiles(_directory, "*.db")
            .Single(file => Path.GetFileName(file) != "metadata.db");
        Assert.Equal((long)ValueColumn.Long, Scalar(partition, "SELECT value_column FROM paths;"));

        // Act
        core.Record("/a/Value", Base.AddSeconds(1), 2.5d, typeof(double));
        await core.FlushAsync(CancellationToken.None);

        // Assert
        Assert.Equal((long)ValueColumn.Double, Scalar(partition, "SELECT value_column FROM paths;"));
        Assert.Equal(1, Scalar(partition, "SELECT COUNT(*) FROM paths;"));
    }
}
