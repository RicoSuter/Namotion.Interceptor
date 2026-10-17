using System.Globalization;
using System.Net.Http.Headers;
using HomeBlaze.History.Abstractions;
using HomeBlaze.History.Import.Influx;
using HomeBlaze.History.Sqlite;

const string Usage = """
    Imports v1 history from InfluxDB 2 into a v2 SQLite history directory.

    Required:
      --mapping <file>             mapping CSV (enabled,confidence,measurement,field,path,type,divisor,values,note)
      --influx-url <url>           InfluxDB server root, for example http://influx:8086
      --since <utc>                start of the imported history and of the coverage it claims
      --target <directory>         SQLite history directory (History/Sqlite); optional for a dry run with --until

    Optional:
      --from <utc>                 read data from here instead of --since (coverage still starts at --since)
      --until <utc>                exclusive end; default: start of the earliest coverage range in --target
      --dry-run                    read InfluxDB and write the report only
      --report <file>              markdown report, default import-report.md
      --partition-interval <name>  Weekly (default), Daily or Monthly; must match the store
      --max-json-size <chars>      must match the store, default 8192
      --org <name>                 default HomeBlaze
      --bucket <name>              default HomeBlaze
      --token-file <file>          default ~/.config/homeblaze/influx.token
      --chunk-days <days>          days per InfluxDB request, default 90
    """;

var options = ParseArguments(args);
if (options is null)
{
    await Console.Error.WriteLineAsync(Usage);
    return 2;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

try
{
    return Run(options, cancellation.Token);
}
catch (Exception exception) when (exception is not OutOfMemoryException)
{
    await Console.Error.WriteLineAsync($"Import failed: {exception.Message}");
    return 1;
}

static int Run(Dictionary<string, string?> options, CancellationToken cancellationToken)
{
    var isDryRun = options.ContainsKey("dry-run");
    var mappingRows = MappingFile.Parse(File.OpenText(Required(options, "mapping")));
    var since = Time(Required(options, "since"));
    var from = options.TryGetValue("from", out var fromText) ? Time(fromText!) : since;
    var chunkLength = TimeSpan.FromDays(double.Parse(options.GetValueOrDefault("chunk-days") ?? "90", CultureInfo.InvariantCulture));

    using var store = OpenStore(options, isDryRun);
    var until = ResolveUntil(options, store);
    if (since > from || from >= until)
    {
        throw new ArgumentException("The range must satisfy --since <= --from < --until.");
    }

    using var influx = CreateInfluxClient(options);
    var importer = new Importer(
        (measurement, field, start, end) => influx.ReadSeries(measurement, field, start, end, cancellationToken),
        isDryRun ? null : store,
        path => store is not null && store.GetMoveChain(path).Count > 1,
        chunkLength,
        Console.Out);

    var statistics = importer.Run(mappingRows, since, from, until, cancellationToken);

    if (!isDryRun)
    {
        // Only once every enabled row is in: inside coverage the store answers alone.
        store!.AddCoverage(new HistoryCoverage(since, until));
    }

    var reportPath = options.GetValueOrDefault("report") ?? "import-report.md";
    using (var report = File.CreateText(reportPath))
    {
        ReportWriter.Write(report, mappingRows, statistics, since, from, until, isDryRun);
    }

    Console.WriteLine($"Report written to {Path.GetFullPath(reportPath)}");
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"The store's MaxAgeDays must be at least {Math.Ceiling((DateTimeOffset.UtcNow - since).TotalDays)} or its retention sweep deletes the imported partitions."));
    return 0;
}

// The store is only opened when it is written to or asked for its coverage, because opening it
// creates the directory and its metadata database.
static SqliteHistoryStore? OpenStore(Dictionary<string, string?> options, bool isDryRun)
{
    var needsStore = !isDryRun || !options.ContainsKey("until");
    if (!needsStore)
    {
        return null;
    }

    var target = options.GetValueOrDefault("target") ??
        throw new ArgumentException("--target is required unless a dry run is given --until.");

    return new SqliteHistoryStore(
        priority: 0,
        databaseDirectory: target,
        Enum.Parse<PartitionInterval>(options.GetValueOrDefault("partition-interval") ?? "Weekly", ignoreCase: true),
        // Never swept here; the store only needs a value.
        maxAge: TimeSpan.FromDays(365 * 100),
        maxJsonSize: int.Parse(options.GetValueOrDefault("max-json-size") ?? "8192", CultureInfo.InvariantCulture),
        getUtcNow: () => DateTimeOffset.UtcNow);
}

static DateTimeOffset ResolveUntil(Dictionary<string, string?> options, SqliteHistoryStore? store)
{
    if (options.TryGetValue("until", out var untilText))
    {
        return Time(untilText!);
    }

    var ranges = store!.CoverageRanges;
    if (ranges.IsDefaultOrEmpty)
    {
        throw new ArgumentException("The target holds no coverage yet, so --until is required.");
    }

    Console.WriteLine($"--until defaults to the start of the existing coverage: {ranges[0].From:O}");
    return ranges[0].From;
}

static InfluxClient CreateInfluxClient(Dictionary<string, string?> options)
{
    var tokenFile = options.GetValueOrDefault("token-file") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "homeblaze", "influx.token");

    // The query path is resolved relative to this, so it has to be the server root.
    var httpClient = new HttpClient
    {
        BaseAddress = new Uri(Required(options, "influx-url")),
        Timeout = TimeSpan.FromHours(1)
    };
    httpClient.DefaultRequestHeaders.Authorization =
        new AuthenticationHeaderValue("Token", File.ReadAllText(tokenFile).Trim());

    return new InfluxClient(
        httpClient,
        options.GetValueOrDefault("org") ?? "HomeBlaze",
        options.GetValueOrDefault("bucket") ?? "HomeBlaze");
}

static Dictionary<string, string?>? ParseArguments(string[] arguments)
{
    var options = new Dictionary<string, string?>(StringComparer.Ordinal);
    var index = 0;
    while (index < arguments.Length)
    {
        var argument = arguments[index++];
        if (!argument.StartsWith("--", StringComparison.Ordinal))
        {
            return null;
        }

        var name = argument[2..];
        if (name == "dry-run")
        {
            options[name] = null;
        }
        else if (index < arguments.Length)
        {
            options[name] = arguments[index++];
        }
        else
        {
            return null;
        }
    }

    return options.ContainsKey("mapping") && options.ContainsKey("since") && options.ContainsKey("influx-url") ? options : null;
}

static string Required(Dictionary<string, string?> options, string name) =>
    options.TryGetValue(name, out var value) && value is not null
        ? value
        : throw new ArgumentException($"--{name} is required.");

static DateTimeOffset Time(string text) =>
    DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
