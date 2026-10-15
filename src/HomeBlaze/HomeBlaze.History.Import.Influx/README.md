# HomeBlaze.History.Import.Influx

Imports HomeBlaze v1 history from InfluxDB 2 into the SQLite history store of HomeBlaze v2. The import is driven by a mapping file, writes through the store's own import API (`SqliteHistoryStore.Import` and `AddCoverage`) and is idempotent: rows already in the store are kept, so running it again with more enabled rows only adds data.

v1 wrote one InfluxDB measurement per thing (its id, with a child path for nested things), one field per property, and the tags `Type` and `Title`. TimeSpan values were written as seconds and enums as their names.

## Mapping file

A CSV file with a header row and these columns:

| Column | Meaning |
|---|---|
| `enabled` | `true` or `false`; only enabled rows are imported |
| `confidence` | free text for review, for example `high` or `medium` |
| `measurement`, `field` | the v1 series |
| `path` | the v2 property path, for example `/Devices/HeatPump/Energy/TotalElectricalEnergy` |
| `type` | the v2 property type: `decimal`, `double`, `long`, `int`, `bool` or `string` (use `string` for enums) |
| `divisor` | optional; numeric values are divided by it, for example `3600` for seconds to hours |
| `values` | optional value map for strings: `from=to;from=to;*=fallback`; without `*`, unmapped values are skipped |
| `note` | free text |

A mapping is specific to an instance, because it names the v1 thing ids and the v2 paths of that installation, so it is kept with the instance rather than here. `Mappings/example.csv` shows the format. A practical way to build one: list the v1 series with their `Type` and `Title` tags, match them to v2 subjects by id or title, compare recent v1 values with the live v2 values, enable the rows that match and leave the uncertain ones disabled with the reason in `note`.

Several rows may map the same series to different paths; the series is then read once. The type must match the v2 property, because it decides the column a value is stored in. The import refuses a path the target already stores under another column.

## Running

```bash
dotnet run --project src/HomeBlaze/HomeBlaze.History.Import.Influx -c Release -- --mapping mapping.csv --influx-url http://influx:8086 --since 2023-07-17T00:00:00Z --until 2026-10-05T00:00:00Z --target ./Sqlite --report report.md
```

| Option | Meaning |
|---|---|
| `--since` | start of the imported history and of the coverage the import claims |
| `--from` | read data from here instead of `--since`; the coverage still starts at `--since` |
| `--until` | exclusive end; defaults to the start of the earliest coverage range already in the target |
| `--target` | the SQLite history directory (`History/Sqlite` of the instance) |
| `--dry-run` | read InfluxDB and write the report without touching the target |
| `--partition-interval`, `--max-json-size` | must match the store's configuration (defaults `Weekly`, `8192`) |
| `--org`, `--bucket`, `--token-file` | InfluxDB organization, bucket and a file holding the API token (defaults `HomeBlaze`, `HomeBlaze`, `~/.config/homeblaze/influx.token`) |
| `--chunk-days` | days per InfluxDB request, default 90 |

The coverage `[--since, --until)` is written only after every enabled row was imported. Inside coverage the store answers queries alone and another store is never asked, so `--until` should be where the live store's own coverage begins: that is the default when the target already holds coverage.

The store's retention deletes partitions older than `MaxAgeDays`. Raise it before starting HomeBlaze on imported files, or the next sweep deletes them; the tool prints the minimum value.

## Moving an instance's history over

SQLite files must not be written over a network share, so the import runs on a local copy:

1. Import everything before the live store's current partition into a local folder, with `--until` set to that partition's start (for weekly partitions, the Monday of the week the live store started in).
2. Stop HomeBlaze and set the SQLite history store's `MaxAgeDays` high enough to keep the imported range.
3. Copy `metadata.db` and the live partition file (for example `2026-10-05.db`) from the instance's `History/Sqlite` into the local folder, replacing the local `metadata.db`.
4. Run the import again with `--from` set to that partition's start and no `--until`. It imports the remaining days up to the start of the live coverage, checks every mapped path against the column the live store uses, and adds the coverage. A path the live store recorded a move into is read again from `--since`, so its older samples are filed under the path it had at the time.
5. Copy all `.db` files back into `History/Sqlite` and start HomeBlaze.
