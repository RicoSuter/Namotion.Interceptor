---
title: HistoryChart
navTitle: HistoryChart
position: 50
---

# HistoryChart

Shows the recorded history of properties in one chart: numbers as lines on one value axis, and states as bands below them, with a legend of the current values. Type name: `HomeBlaze.History.Blazor.HistoryChart`. Slot blocks: `Series`.

## Arguments

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `Range` | `1h`, `6h`, `24h`, `7d` or `30d` | `24h` | The time span that ends now |
| `Bucket` | `Auto`, `Raw`, `1s`, `10s`, `60s`, `5m`, `10m`, `15m`, `1h`, `4h`, `6h`, `12h` or `24h` | `Auto` | The interval that one point of a line stands for. `Auto` picks one from the range, `Raw` shows every recorded sample. A fixed bucket must divide the range into at most 1000 intervals, for example at least `10s` for `1h` and `1h` for `30d`. The bucket applies to lines only, a band always shows the recorded samples |
| `Aggregation` | `TimeWeightedAverage` (or `Average`), `SampleAverage`, `Minimum`, `Maximum`, `Sum`, `StandardDeviation`, `Last`, `First` or `Count` | Automatic | How the values in a bucket become one point of a line, see [Behaviour and Limits](#behaviour-and-limits). Without it a line takes the time-weighted average when a history store offers it, and a counter its last value. A band ignores it, and with `Bucket=Raw` it does not apply, because every recorded sample is shown. An aggregation that the property or the history stores do not offer, for example `Sum` for a counter, falls back to the automatic one, and the legend says so |
| `Height` | Whole number above 0 | `300` | The height of the lines in pixels. The lines fill the width of the chart, and on a narrow screen they get lower to stay at least twice as wide as high |
| `RefreshInterval` | Time span up to a day, for example `00:05:00` | `00:01:00` | How often the chart queries again at most while its values change. Less than a minute counts as a minute, and a bucket longer than the interval lengthens it to the bucket, see [Behaviour and Limits](#behaviour-and-limits) |
| `Compact` | Yes or no | `false` | Leaves out the numbers of the value axis, the times of the time axis, the grid lines and the tooltips of the lines, so the lines fill the whole width. For a small chart next to others, whose detail is in the history dialog |

`Range`, `Bucket` and `Aggregation` ignore case.

## Slots

| Slot | Blocks | Holds |
|------|--------|-------|
| `Series` | Any number | One series per block, in the order of the legend. It holds no content |

The arguments of a `Series` block:

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `Path` | Path (Number, Boolean or Enum) | None | The [path](basics.md#paths) of the property whose history the series shows. A number is a line, and a boolean, an enum, a text and a number whose state is marked as discrete are a band. The picker offers no text, so type the path of a text |
| `Label` | Text | From the property, see [Labels](basics.md#labels) | The label in the legend of the lines or above a band |
| `Color` | Text, a CSS colour such as `"#E4572E"` | The next colour of the chart | The colour of a line. A band has one colour per state and ignores it |
| `Aggregation` | Like `Aggregation` of the chart | The aggregation of the chart | The aggregation of this line in place of the one of the chart. A band ignores it |

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

The status of the pump as a band below its speed. The band has one colour per state, and its header names its current value on the left and its states on the right. A status that has not changed since HomeBlaze started shows "No data" until it changes, see [Behaviour and Limits](#behaviour-and-limits):

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

The highest recorded sample of the speed of the pump in each ten minutes of the last six hours, next to its average. Ten minutes without a recorded sample are a gap in the highest speed:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.History.Blazor.HistoryChart Range=6h Bucket=10m Aggregation=Maximum Height=200
::: Series Path=../Pump/CurrentSpeed Label="Highest speed"
::: Series Path=../Pump/CurrentSpeed Label="Average speed" Aggregation=TimeWeightedAverage
::
~~~~

:: HomeBlaze.History.Blazor.HistoryChart Range=6h Bucket=10m Aggregation=Maximum Height=200
::: Series Path=../Pump/CurrentSpeed Label="Highest speed"
::: Series Path=../Pump/CurrentSpeed Label="Average speed" Aggregation=TimeWeightedAverage
::

Three compact charts in a row: the temperature of each motor and the speed of the pump. A click on a chart opens the history dialog of its series:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.FlowLayout MinWidth=180
::: Item
:::: HomeBlaze.History.Blazor.HistoryChart Range=1h Height=100 Compact=true
::::: Series Path=../Pump/Temperature Label="Pump"
::: Item
:::: HomeBlaze.History.Blazor.HistoryChart Range=1h Height=100 Compact=true
::::: Series Path=../Fan/Temperature Label="Fan"
::: Item
:::: HomeBlaze.History.Blazor.HistoryChart Range=1h Height=100 Compact=true
::::: Series Path=../Pump/CurrentSpeed Label="Pump speed"
::
~~~~

:: HomeBlaze.Components.FlowLayout MinWidth=180
::: Item
:::: HomeBlaze.History.Blazor.HistoryChart Range=1h Height=100 Compact=true
::::: Series Path=../Pump/Temperature Label="Pump"
::: Item
:::: HomeBlaze.History.Blazor.HistoryChart Range=1h Height=100 Compact=true
::::: Series Path=../Fan/Temperature Label="Fan"
::: Item
:::: HomeBlaze.History.Blazor.HistoryChart Range=1h Height=100 Compact=true
::::: Series Path=../Pump/CurrentSpeed Label="Pump speed"
::

## Behaviour and Limits

- **One unit**: All lines share one value axis, so every recorded number series needs the unit of the first one. A series with another unit, for example a temperature next to a speed, shows an error that names both series in place of the chart, see [Value Widget Errors](../page-errors.md#value-widget-errors). Put it in a second chart. A line with the aggregation `Count` counts samples and has no unit, unless the bucket is `Raw`.
- **Percent**: A percent value is recorded as a fraction from 0 to 1 and drawn times 100, on a value axis from 0 % to 100 % that grows only when a value leaves that range. The history dialog draws it the same way, and its count of samples stays a count.
- **States as bands**: A boolean, an enum, a text and a number whose state is marked as discrete (`IsDiscrete` of its `[State]` attribute) are a band below the lines, one per series with a header above it: the label and the current value, and the states that the band shows. Below lines a band starts and ends where the lines do, so a change of state sits under its time. A chart without lines names its range below the bands. A state has the same colour in every band of a chart, and "Yes" is always blue. A band is always queried as recorded samples, together with the value held when the range starts, whatever the bucket.
- **"No data" and "(no value)"**: A hatched span marked "No data" in a band has no known value: no history store recorded it, or nothing was recorded in it since recording started. "(no value)" is a value that was recorded as empty. A value never reaches across a span that no store recorded. Over a long range a band holds only the newest samples up to a limit, and the span before the oldest of them shows "No data". The [history design](../../architecture/design/history.md#state-timeline) has the details.
- **Point limit**: A query returns at most 1000 points of a series. A band, or a line with `Bucket=Raw`, with more recorded samples in the range shows the newest of them, and its legend entry says "(oldest samples not shown)". A fixed bucket that would divide the range into more intervals is an error, and `Auto` never picks one.
- **Recorded on change**: History is recorded when a value changes. A value that has not changed since HomeBlaze started shows "No data" in a band until it changes.
- **Gaps in a line**: A span that no history store has recorded is a gap in a line.
- **Legend**: The legend below the lines lists every line with its current value, and the header of each band its current value, which follow every change at once. A band series without a band, for example one without history, is listed with the lines.
- **Refresh**: The chart queries again at most once per window, and only while one of its values changes. The window is the refresh interval, at least a minute, or the bucket when the bucket is longer. A changed series, range or bucket queries at once. After the first query of a chart, and after one at once, it queries once more one full window later, because the history stores may not have held the latest values yet. A chart that nobody views costs nothing.
- **No history**: A series whose property is not recorded shows "no history recorded" in the legend, and a path that leads nowhere shows "not found". Without any history store every series shows "no history store". None of these is an error. Only `[State]` properties of a number, a boolean, an enum or a text are recorded.
- **Aggregation**: A line whose aggregation the property or the history stores do not offer shows the automatic one, with a note in the legend that names both, for example "(Sum not available, shows Last)" for a counter. This is no error, because stores offer different aggregations. `Minimum`, `Maximum`, `Sum`, `SampleAverage`, `StandardDeviation` and `First` read only the samples recorded inside a bucket and ignore the value held into it: a bucket without a recorded sample is a gap in the line, and the maximum of a bucket can be below its time-weighted average. `Count` is 0 for such a bucket. `Last` and the time-weighted average carry the value held into a bucket, see [Query semantics](../../architecture/design/history.md#query-semantics).
- **Errors**: An unknown `Range`, `Bucket` or `Aggregation`, a fixed `Bucket` too small for the range, a `Height` of 0 or less, a `RefreshInterval` longer than a day, or a line with another unit, shows a small red alert in place of the chart and its legend, see [Value Widget Errors](../page-errors.md#value-widget-errors). A query that fails shows the warning `Could not load history` with the reason in place of the chart, the legend stays, and the chart tries again with the next refresh.
- **History dialog**: A click on the lines opens the history dialog of the first line, and a click on an entry of the legend or in the header of a band the dialog of that series. The dialog shows the same chart for that one property with its axes, with a choice of range, bucket and aggregation, and opens with the range, bucket and aggregation of the chart. A band opens it with the range of the chart and its recorded states. The lines open it with a mouse click only; with the keyboard, the entries of the legend and of the band headers are buttons that open the same dialogs. The history link of a property in the browser opens the same dialog. In edit mode a click selects the chart instead.

## Related

- [Basics](basics.md) - Type names, paths and colours
- [Overview](overview.md) - All widgets by group
- [History design](../../architecture/design/history.md) - How history is recorded and queried
