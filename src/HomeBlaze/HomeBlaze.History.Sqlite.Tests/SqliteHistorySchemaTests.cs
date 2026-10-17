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
    public async Task WhenAStoreOnlyReads_ThenItLeavesEveryDatabaseUnwritten()
    {
        // Arrange - write some partitions, then let a sweep fold their WAL back so every -wal is empty.
        using (var writer = NewCore(Base.AddSeconds(60)))
        {
            writer.Record("/a/Value", Base, 1.5d, typeof(double));
            writer.Record("/b/Value", Base.AddSeconds(1), 2.5d, typeof(double));
            await writer.FlushAsync(CancellationToken.None);
            writer.Sweep();
        }

        // Act - a fresh store that only queries. Opening a database used to re-stamp application_id and
        // user_version unconditionally, and both write page 1, so merely reading dirtied every file the
        // open path touched and handed the next sweep a checkpoint it should never have needed.
        using (var reader = NewCore(Base.AddSeconds(120)))
        {
            reader.Query(new HistoryQuery("/a/Value", Base, Base.AddSeconds(60)));
            reader.Query(new HistoryQuery("/never/Written", Base, Base.AddSeconds(60)));

            // Assert - while the connections are still open, so the sizes reflect the reads themselves.
            var dirty = Directory.EnumerateFiles(_directory, "*.db-wal")
                .Where(file => new FileInfo(file).Length > 0)
                .Select(Path.GetFileName)
                .ToArray();
            Assert.Empty(dirty);
        }
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
    public async Task WhenSamplesArriveOutOfOrderWithinOneFlush_ThenTheNewestOneOwnsTheStoredKind()
    {
        // Arrange - the pending list is in arrival order, so a device reporting late puts an older
        // sample after a newer one. Interning from whichever arrived last then stored the superseded
        // kind. Here the newer sample is a ulong large enough to overflow into value_json, which only
        // reads back as a number when the stored flag says the property is ulong.
        using var core = NewCore(Base.AddSeconds(60));
        core.Record("/a/Value", Base.AddSeconds(1), ulong.MaxValue, typeof(ulong)); // newer, arrives first
        core.Record("/a/Value", Base, 7L, typeof(long));                            // older, arrives second

        // Act
        await core.FlushAsync(CancellationToken.None);

        // Assert
        var partition = Directory.EnumerateFiles(_directory, "*.db")
            .Single(file => Path.GetFileName(file) != "metadata.db");
        Assert.Equal(1, Scalar(partition, "SELECT is_ulong FROM paths;"));

        var series = core.Query(new HistoryQuery("/a/Value", Base, Base.AddSeconds(10)));
        Assert.Equal(
            new double?[] { 7d, ulong.MaxValue },
            series.Points.Select(point => point.Number).ToArray());
    }

    [Fact]
    public void WhenTheFileIsNotAHistoryDatabase_ThenItIsRefusedRatherThanAdopted()
    {
        // Arrange - a SQLite file that happens to sit in the history directory under a partition-shaped
        // name. Checking only the version let anything with tables and an unexpected version through:
        // it was restamped as a history database and its own tables left in place.
        Directory.CreateDirectory(_directory);
        var foreign = Path.Combine(_directory, "2026-06-22.db");
        using (var connection = OpenDirectly(foreign))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE something_else (id INTEGER); PRAGMA user_version=7;";
            command.ExecuteNonQuery();
        }

        // Act - a read spanning that file must not fail on it. The sweep deletes by age and never by
        // version, so a partition this build cannot read would otherwise break every query for as long as
        // retention keeps it.
        using var core = NewCore(Base.AddSeconds(60));
        var series = core.Query(new HistoryQuery("/a/Value", Base, Base.AddSeconds(10)));

        // Assert - skipped, reported, and above all not adopted: still the foreign file's own version and
        // tables, with none of this store's written into it.
        Assert.Empty(series.Points);
        Assert.Contains("Delete the history directory", core.LastError);
        Assert.Equal(7L, Scalar(foreign, "PRAGMA user_version;"));
        Assert.Equal(1L, Scalar(foreign, "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'something_else';"));
        Assert.Equal(0L, Scalar(foreign, "SELECT COUNT(*) FROM sqlite_schema WHERE name IN ('history', 'paths');"));
    }

    [Fact]
    public async Task WhenADoubleIsNotANumber_ThenItStaysDistinguishableFromARecordedNull()
    {
        // Arrange - SQLite binds NaN into a REAL column as SQL NULL, and a row whose three value columns
        // are all null is exactly how an explicitly recorded null is stored. Written that way the two are
        // the same bytes on disk, so nothing afterwards could ever tell them apart.
        using var core = NewCore(Base.AddSeconds(60));
        core.Record("/a/Value", Base, double.NaN, typeof(double));
        core.Record("/a/Value", Base.AddSeconds(1), null, typeof(double));

        // Act
        await core.FlushAsync(CancellationToken.None);

        // Assert
        var partition = Directory.EnumerateFiles(_directory, "*.db")
            .Single(file => Path.GetFileName(file) != "metadata.db");
        Assert.Equal(1L, Scalar(partition,
            "SELECT COUNT(*) FROM history WHERE value_json = '\"NaN\"' AND value_double IS NULL;"));
        Assert.Equal(1L, Scalar(partition,
            "SELECT COUNT(*) FROM history WHERE value_long IS NULL AND value_double IS NULL AND value_json IS NULL;"));
    }

    [Fact]
    public async Task WhenAPropertyChangesTypeWithinOneFlush_ThenTheStoredColumnKindStillFollowsIt()
    {
        // Arrange - the same type change as the test below, but with no flush between the two samples.
        // Resolving a path once per batch and caching only its id let the first sample own the stored
        // kind, so whether the newest kind survived depended on where the flush boundary happened to
        // fall. Here the ulong is stored as an overflow in value_json, which only reads back as a number
        // when the stored flag says the property is ulong.
        using var core = NewCore(Base.AddSeconds(60));
        core.Record("/a/Value", Base, 7L, typeof(long));
        core.Record("/a/Value", Base.AddSeconds(1), ulong.MaxValue, typeof(ulong));

        // Act
        await core.FlushAsync(CancellationToken.None);

        // Assert
        var partition = Directory.EnumerateFiles(_directory, "*.db")
            .Single(file => Path.GetFileName(file) != "metadata.db");
        Assert.Equal(1, Scalar(partition, "SELECT is_ulong FROM paths;"));

        var series = core.Query(new HistoryQuery("/a/Value", Base, Base.AddSeconds(10)));
        Assert.Equal(
            new double?[] { 7d, ulong.MaxValue },
            series.Points.Select(point => point.Number).ToArray());
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

    [Fact]
    public async Task WhenALateSampleOfTheOldTypeIsFlushed_ThenTheStoredColumnKindKeepsTheNewestType()
    {
        // Arrange - the property changed from string to double, then a device reports a string sample
        // older than the doubles already in the file. The file's newest row is still a double. The store
        // opens before the first sample, so the samples are inside its coverage.
        var now = Base.AddSeconds(-60);
        using var core = new SqliteHistoryStore(
            priority: 50, databaseDirectory: _directory, PartitionInterval.Weekly,
            TimeSpan.FromHours(1), maxJsonSize: 8192, () => now);
        now = Base.AddSeconds(60);
        core.Record("/a/Value", Base, "on", typeof(string));
        await core.FlushAsync(CancellationToken.None);
        core.Record("/a/Value", Base.AddSeconds(10), 1.5d, typeof(double));
        core.Record("/a/Value", Base.AddSeconds(20), 2.5d, typeof(double));
        await core.FlushAsync(CancellationToken.None);

        // Act
        core.Record("/a/Value", Base.AddSeconds(5), "off", typeof(string));
        await core.FlushAsync(CancellationToken.None);

        // Assert
        var partition = Directory.EnumerateFiles(_directory, "*.db")
            .Single(file => Path.GetFileName(file) != "metadata.db");
        Assert.Equal((long)ValueColumn.Double, Scalar(partition, "SELECT value_column FROM paths;"));

        var maximum = core.Query(new HistoryQuery(
            "/a/Value", Base, Base.AddSeconds(30), TimeSpan.FromSeconds(30), HistoryAggregations.Maximum));
        Assert.Equal(new double?[] { 2.5 }, maximum.Points.Select(point => point.Number).ToArray());

        var raw = core.Query(new HistoryQuery("/a/Value", Base, Base.AddSeconds(30)));
        Assert.Equal(
            new double?[] { null, null, 1.5, 2.5 },
            raw.Points.Select(point => point.Number).ToArray());
    }

    [Fact]
    public async Task WhenALateSampleOfTheOldTypeIsFlushedAfterAUlongChange_ThenRawQueryReadsTheOverflowAsANumber()
    {
        // Arrange - a ulong above long.MaxValue is stored in value_json and only reads back as a number
        // when the stored flag says the property is ulong, so a late long sample must not clear it.
        using var core = NewCore(Base.AddSeconds(60));
        core.Record("/a/Value", Base, 7L, typeof(long));
        await core.FlushAsync(CancellationToken.None);
        core.Record("/a/Value", Base.AddSeconds(10), ulong.MaxValue, typeof(ulong));
        await core.FlushAsync(CancellationToken.None);

        // Act
        core.Record("/a/Value", Base.AddSeconds(5), 8L, typeof(long));
        await core.FlushAsync(CancellationToken.None);

        // Assert
        var series = core.Query(new HistoryQuery("/a/Value", Base, Base.AddSeconds(30)));
        Assert.Equal(
            new double?[] { 7, 8, ulong.MaxValue },
            series.Points.Select(point => point.Number).ToArray());
    }

    [Fact]
    public async Task WhenTheTypeChangesBackWithANewerSample_ThenTheStoredColumnKindFollowsIt()
    {
        // Arrange - a late string sample left the double kind in place; a string sample newer than every
        // double is a genuine type change and still updates the kind.
        using var core = NewCore(Base.AddSeconds(60));
        core.Record("/a/Value", Base, "on", typeof(string));
        await core.FlushAsync(CancellationToken.None);
        core.Record("/a/Value", Base.AddSeconds(10), 1.5d, typeof(double));
        await core.FlushAsync(CancellationToken.None);
        core.Record("/a/Value", Base.AddSeconds(5), "off", typeof(string));
        await core.FlushAsync(CancellationToken.None);

        // Act
        core.Record("/a/Value", Base.AddSeconds(20), "on", typeof(string));
        await core.FlushAsync(CancellationToken.None);

        // Assert
        var partition = Directory.EnumerateFiles(_directory, "*.db")
            .Single(file => Path.GetFileName(file) != "metadata.db");
        Assert.Equal((long)ValueColumn.Json, Scalar(partition, "SELECT value_column FROM paths;"));
        Assert.Throws<HistoryAggregationNotSupportedException>(() => core.Query(new HistoryQuery(
            "/a/Value", Base, Base.AddSeconds(30), TimeSpan.FromSeconds(30), HistoryAggregations.Maximum)));
    }

    [Fact]
    public async Task WhenTheNewestRowIsReplacedWithANewType_ThenTheStoredColumnKindFollowsIt()
    {
        // Arrange - a sample at the same timestamp as the file's newest row replaces that row, so its
        // type becomes the newest one.
        using var core = NewCore(Base.AddSeconds(60));
        core.Record("/a/Value", Base, 1.5d, typeof(double));
        await core.FlushAsync(CancellationToken.None);

        // Act
        core.Record("/a/Value", Base, "on", typeof(string));
        await core.FlushAsync(CancellationToken.None);

        // Assert
        var partition = Directory.EnumerateFiles(_directory, "*.db")
            .Single(file => Path.GetFileName(file) != "metadata.db");
        Assert.Equal((long)ValueColumn.Json, Scalar(partition, "SELECT value_column FROM paths;"));
    }
}
