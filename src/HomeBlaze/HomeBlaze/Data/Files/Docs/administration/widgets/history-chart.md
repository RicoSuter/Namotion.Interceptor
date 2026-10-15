---
title: HistoryChart
navTitle: HistoryChart
position: 50
---

# HistoryChart

Shows the recorded history of properties in one chart: numbers as lines on one value axis, and states as bands below them, with a legend of the current values. Type name: `HomeBlaze.History.Blazor.HistoryChart`. Its slot block is `Series`, one per property.

## Arguments

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `Range` | `1h`, `6h`, `24h`, `7d` or `30d` | `24h` | The time span that ends now |
| `Bucket` | `Auto`, `Raw`, `1s`, `10s`, `60s`, `5m`, `10m`, `15m`, `1h`, `4h`, `6h`, `12h` or `24h` | `Auto` | The interval that one point of a line stands for. `Auto` picks one from the range, `Raw` shows every recorded sample. The bucket applies to lines only, a band always shows the recorded samples |
| `Height` | Whole number | `300` | The height of the lines in pixels. The lines fill the width of the chart, and on a narrow screen they get lower to stay at least twice as wide as high |
| `RefreshInterval` | Time span, for example `00:05:00` | `00:01:00` | How often the chart queries again at most while its values change. Less than a minute counts as a minute |

`Range` and `Bucket` ignore case.

## Slots

| Slot | Blocks | Holds |
|------|--------|-------|
| `Series` | Any number | One series per block, in the order of the legend |

The arguments of a `Series` block:

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `Path` | Path (Number, Boolean or Enum) | None | The path of the property whose history the series shows. A number is a line, a boolean or an enum a band. The picker offers these kinds only |
| `Label` | Text | The title of the property, then its name | The label in the legend and above a band. Without it the series shows the title that the property declares, the name of the property when it declares none, and the path when the path leads nowhere |
| `Color` | Text, a CSS colour such as `"#E4572E"` | The next colour of the chart | The colour of a line. A band has one colour per state and ignores it |

## Samples

The samples show the history of the sample motors `../Pump` and `../Fan` next to this page. A fresh installation has recorded little yet, so these charts start nearly empty and fill in over the following hours.

The speed of both motors in one chart. Neither speed has a unit, so they share the value axis:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.History.Blazor.HistoryChart
::: Series Path=../Pump/CurrentSpeed Label="Pump"
::: Series Path=../Fan/CurrentSpeed Label="Fan"
::
~~~~

:: HomeBlaze.History.Blazor.HistoryChart
::: Series Path=../Pump/CurrentSpeed Label="Pump"
::: Series Path=../Fan/CurrentSpeed Label="Fan"
::

The status of the pump as a band below its speed. The band has one colour per state, and the legend below it names them. A status that has not changed since HomeBlaze started shows "No data" until it changes, see [Behaviour and Limits](#behaviour-and-limits):

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.History.Blazor.HistoryChart Range=6h Height=200
::: Series Path=../Pump/CurrentSpeed Label="Pump speed"
::: Series Path=../Pump/Status Label="Pump status"
::
~~~~

:: HomeBlaze.History.Blazor.HistoryChart Range=6h Height=200
::: Series Path=../Pump/CurrentSpeed Label="Pump speed"
::: Series Path=../Pump/Status Label="Pump status"
::

The last hour with the bucket that the chart picks from the range, a colour of its own and a refresh every five minutes at most:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.History.Blazor.HistoryChart Range=1h Bucket=Auto RefreshInterval=00:05:00
::: Series Path=../Fan/Temperature Label="Fan temperature" Color="#E4572E"
::
~~~~

:: HomeBlaze.History.Blazor.HistoryChart Range=1h Bucket=Auto RefreshInterval=00:05:00
::: Series Path=../Fan/Temperature Label="Fan temperature" Color="#E4572E"
::

## Behaviour and Limits

- **One unit**: All lines share one value axis, so every number series needs the unit of the first one. A series with another unit, for example a temperature next to a speed, shows an error that names both series in place of the chart, see [Value Widget Errors](../page-errors.md#value-widget-errors). Put it in a second chart.
- **States as bands**: A boolean, an enum or a text is a band below the lines, one per series with its label above it. A state has the same colour in every band of a chart, and "Yes" is always blue. A band is always queried as recorded samples, together with the value held when the range starts, whatever the bucket.
- **"No data" and "(no value)"**: A hatched span marked "No data" in a band has no known value: no history store recorded it, or nothing was recorded in it since recording started. "(no value)" is a value that was recorded as empty. A value never reaches across a span that no store recorded. Over a long range a band holds only the newest samples up to a limit, and the span before the oldest of them shows "No data". The [history design](../../architecture/design/history.md#state-timeline) has the details.
- **Recorded on change**: History is recorded when a value changes. A value that has not changed since HomeBlaze started shows "No data" in a band until it changes.
- **Gaps in a line**: A span that no history store has recorded is a gap in a line.
- **Legend**: The legend lists every series with its current value, which follows every change at once.
- **Refresh**: The chart queries again at most once per window, and only while one of its values changes. The window is the refresh interval, at least a minute, or the bucket when the bucket is longer. A changed series, range or bucket queries at once. A chart that nobody views costs nothing.
- **No history**: A series whose property is not recorded shows "no history recorded" in the legend, and a path that leads nowhere shows "not found". Without any history store every series shows "no history store". None of these is an error. Only `[State]` properties of a number, a boolean, an enum or a text are recorded.
- **Errors**: An unknown `Range` or `Bucket`, or a line with another unit, shows a small red alert in place of the chart and its legend, see [Value Widget Errors](../page-errors.md#value-widget-errors). A query that fails shows a warning "Could not load history" with the reason in place of the chart, the legend stays, and the chart tries again with the next refresh.
- **History dialog**: The history link of a property in the browser opens the same chart for that one property, with a choice of range, bucket and aggregation.

## Related

- [Basics](basics.md) - Type names, paths and colours
- [Overview](overview.md) - All widgets
- [History design](../../architecture/design/history.md) - How history is recorded and queried
