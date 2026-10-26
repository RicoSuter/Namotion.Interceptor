---
title: HistoryChart
navTitle: HistoryChart
---

# HistoryChart

Shows the recorded history of properties in one chart: numbers as lines on one value axis, the increase of a counter per day, week or month as bars, and states as bands below them, with a legend of the current values. Type name: `HomeBlaze.History.Blazor.HistoryChart`. Slot blocks: `Series`.

## Arguments

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `Range` | `1h`, `6h`, `24h`, `7d`, `30d`, `90d` or `1y` (365 days) | `24h` | The time span that ends now. With a calendar bucket, also one that `Auto` picks for bars, it starts at the start of a period, see [Calendar buckets](#behavior-and-limits) |
| `Bucket` | `Auto`, `Raw`, `1s`, `10s`, `60s`, `5m`, `10m`, `15m`, `1h`, `4h`, `6h`, `12h`, `24h`, `day`, `week` or `month` | `Auto` | The interval that one point of a line or one bar stands for. `Auto` picks one from the range, `Raw` shows every recorded sample. A fixed bucket must divide the range into at most 1000 intervals, for example at least `10s` for `1h`, `1h` for `30d` and `12h` for `1y`. `day`, `week` (from Monday) and `month` are calendar periods that start at midnight in the time zone of the viewer, and fit every range. The bucket applies to lines and bars only, a band always shows the recorded samples |
| `Aggregation` | `TimeWeightedAverage` (or `Average`), `SampleAverage`, `Minimum`, `Maximum`, `Sum`, `StandardDeviation`, `Last`, `First`, `Count` or `Delta` | Automatic | How the values in a bucket become one point of a line, see [Behavior and Limits](#behavior-and-limits). `Delta` is the increase of a counter in each bucket. Without it a line takes the time-weighted average when a history store offers it, and a counter its last value. A band ignores it, and with `Bucket=Raw` it does not apply, because every recorded sample is shown. An aggregation that the property or the history stores do not offer, for example `Sum` for a counter or `Delta` for a number that is no counter, falls back to the automatic one, and the legend says so |
| `Height` | Whole number above 0 | `300` | The height of the lines in pixels. The lines fill the width of the chart, and on a narrow screen they get lower to stay at least twice as wide as high |
| `RefreshInterval` | Time span up to a day, for example `00:05:00` | `00:01:00` | How often the chart queries again at most while its values change. Less than a minute counts as a minute, and a fixed bucket longer than the interval lengthens it to the bucket, see [Behavior and Limits](#behavior-and-limits) |
| `Compact` | Yes or no | `false` | Leaves out the numbers of the value axis, the times of the time axis, the grid lines and the tooltips of the lines or bars, so they fill the whole width. For a small chart next to others, whose detail is in the history dialog |

`Range`, `Bucket` and `Aggregation` ignore case.

## Slots

| Slot | Blocks | Holds |
|------|--------|-------|
| `Series` | Any number | One series per block, in the order of the legend. It holds no content |

The arguments of a `Series` block:

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `Path` | Path (Number, Boolean or Enum) | None | The [path](basics.md#paths) of the property whose history the series shows. A number is a line, or a bar with `Delta`, and a boolean, an enum, a text and a number whose state is marked as discrete are a band. The picker offers no text, so type the path of a text |
| `Label` | Text | From the property, see [Labels](basics.md#labels) | The label in the legend of the lines or above a band |
| `Color` | Text, a CSS color such as `"#E4572E"`, `teal` or `"rgb(228, 87, 46)"` | The next color of the chart | The color of a line or a bar. A value other than `#` with 3, 4, 6 or 8 hex digits, a word of letters such as a color name, or `rgb()`, `rgba()`, `hsl()` or `hsla()` with plain numbers uses the next color of the chart. A word the browser does not know as a color leaves the line without a color. A band has one color per state and ignores it |
| `Aggregation` | Like `Aggregation` of the chart | The aggregation of the chart | The aggregation of this line in place of the one of the chart. A band ignores it |
| `Negate` | Yes or no | `false` | Draws the line or bar with the opposite sign, for example the energy fed into the grid below the axis, under the bars of the energy taken from it. The legend and the history dialog show the property as it is. A band ignores it |

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

The status of the pump as a band below its speed. The band has one color per state, and its header names its current value on the left and its states on the right. A status that has not changed since HomeBlaze started shows "No data" until it changes, see [Behavior and Limits](#behavior-and-limits):

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

The last hour with the bucket that the chart picks from the range, a color of its own and a refresh every five minutes at most:

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

The energy that each motor used per day in the last week, as two bars per day. `Energy` is a counter in Wh that only grows, and `Delta` takes its increase in each day, from midnight to midnight in your time zone. The bars are labeled in kWh once a day reaches 1000 Wh. A day before the motors were recorded has no bar:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.History.Blazor.HistoryChart Range=7d Bucket=day Aggregation=Delta Height=200
::: Series Path=../Pump/Energy Label="Pump"
::: Series Path=../Fan/Energy Label="Fan"
::
~~~~

:: HomeBlaze.History.Blazor.HistoryChart Range=7d Bucket=day Aggregation=Delta Height=200
::: Series Path=../Pump/Energy Label="Pump"
::: Series Path=../Fan/Energy Label="Fan"
::

Energy in both directions per day, the one direction below the axis with `Negate`. A grid meter has a counter for each direction, `TotalImportedEnergy` and `TotalExportedEnergy`, and both only grow, so each takes `Delta`. Here the pump stands in for the energy taken from the grid and the fan for the energy fed into it:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.History.Blazor.HistoryChart Range=7d Bucket=day Aggregation=Delta Height=200
::: Series Path=../Pump/Energy Label="Taken"
::: Series Path=../Fan/Energy Label="Fed in" Negate=true
::
~~~~

:: HomeBlaze.History.Blazor.HistoryChart Range=7d Bucket=day Aggregation=Delta Height=200
::: Series Path=../Pump/Energy Label="Taken"
::: Series Path=../Fan/Energy Label="Fed in" Negate=true
::

The energy of the pump per hour of the last day, which fills in sooner on a fresh installation:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.History.Blazor.HistoryChart Range=24h Bucket=1h Aggregation=Delta Height=200
::: Series Path=../Pump/Energy Label="Pump energy"
::
~~~~

:: HomeBlaze.History.Blazor.HistoryChart Range=24h Bucket=1h Aggregation=Delta Height=200
::: Series Path=../Pump/Energy Label="Pump energy"
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

## Behavior and Limits

- **One unit**: All lines share one value axis, so every recorded number series needs the unit of the first one. A series with another unit, for example a temperature next to a speed, shows an error that names both series in place of the chart, see [Value Widget Errors](../page-errors.md#value-widget-errors). Put it in a second chart. A line with the aggregation `Count` counts samples and has no unit, unless the bucket is `Raw`.
- **Percent**: A percent value is recorded as a fraction from 0 to 1 and drawn times 100, on a value axis from 0 % to 100 % that grows only when a value leaves that range. The history dialog draws it the same way, and its count of samples stays a count.
- **Small differences**: The value axis steps by at least 1, so lines whose values differ by only a fraction of 1, for example from 0.25 to 0.30, are drawn nearly flat.
- **Bars**: A line whose aggregation is `Delta` shows the increase of a counter in each bucket, which a chart draws as bars, one group per bucket with one bar per series. The newest bar holds the current period so far and grows with every refresh. A chart draws bars only when all of its lines show `Delta`; with one line of another aggregation, every line is drawn as a line. The value axis of the bars reaches from 0, down as well for a negated series, and names the unit of the counter, scaled to the largest bar, for example kWh for a counter in Wh, and the tooltip of a bar names its value, its series and its period. `Bucket=Auto` picks at most about 50 bars rather than the 200 points of a line, in calendar days, weeks or months once a bar spans a day or more.
- **Delta**: `Delta` applies only to a counter, a property whose `[State]` attribute has `IsCumulative`, such as the energy of a meter. For any other property it falls back to the automatic aggregation, and the legend says so. A drop of the counter counts as a restart from 0, and a span that no history store recorded is bridged, see [Query semantics](../../architecture/design/history.md#query-semantics). A bucket without a known reading has no bar, like one without an increase. Every drop is a restart, also one of a single Wh, so a counter that steps back a little, for example after a correction of the meter, shows its whole reading as one bar that day.
- **Calendar buckets**: `day`, `week` and `month` start at midnight in the time zone of the viewer, the one of the browser or the one chosen in the settings, so a day the clocks change is 23 or 25 hours long. With them the chart shows as many whole periods as the range lasts on average, up to the current one: `Range=7d Bucket=day` shows today and the six days before it, and `Range=1y Bucket=month` this month and the 11 before it. A range shorter than the period shows the current period. Until the browser has named its time zone the periods are those of UTC, and the chart queries again once it has.
- **States as bands**: A boolean, an enum, a text and a number whose state is marked as discrete (`IsDiscrete` of its `[State]` attribute) are a band below the lines, one per series with a header above it: the label and the current value, and the states that the band shows. The header is one line: states that do not fit are cut off at its end, and its tooltip names all of them. Below lines a band starts and ends where the lines do, so a change of state sits under its time. A chart without lines names its range below the bands. A state has the same color in every band of a chart, and "Yes" is always blue. A band is always queried as recorded samples, together with the value held when the range starts, whatever the bucket.
- **"No data" and "(no value)"**: A hatched span marked "No data" in a band has no known value: no history store recorded it, or nothing was recorded in it since recording started. "(no value)" is a value that was recorded as empty. A value never reaches across a span that no store recorded. Over a long range a band holds only the newest samples up to a limit, and the span before the oldest of them shows "No data". The [history design](../../architecture/design/history.md#state-timeline) has the details.
- **Point limit**: A query returns at most 1000 points of a series. A band, or a line with `Bucket=Raw`, with more recorded samples in the range shows the newest of them, and its legend entry says "(oldest samples not shown)". A fixed bucket that would divide the range into more intervals is an error, and `Auto` never picks one.
- **Recorded on change**: History is recorded when a value changes. A value that has not changed since HomeBlaze started shows "No data" in a band until it changes.
- **Gaps in a line**: A span that no history store has recorded is a gap in a line. A `Count` or `Sum` bucket that the history stores recorded only partly is a gap too, rather than a too-low value.
- **Legend**: The legend below the lines lists every line with its current value, and the header of each band its current value, which follow every change at once. A band series without a band, for example one without history, is listed with the lines.
- **Loading**: The chart has the height it keeps with its history from the moment the page opens, so nothing below it moves while the history loads. Until then the room of its lines and bands stays empty, while the legend already shows the current values, and the lines and bands appear once they are drawn in their final place. The room is taken from the series: one area for the lines when at least one line has history, and one band per state series with history. Lines without a point in the range keep their area and say "No data in this range." in it.
- **Refresh**: The chart queries again at most once per window, and only while one of its values changes. The window is the refresh interval, at least a minute, or a fixed bucket when the bucket is longer. A calendar bucket does not lengthen it, so the bar of the current period follows the refresh interval. Within one period, a refresh of bars per day, week or month reads again only from the period that was still open a few minutes before the previous query and keeps the older bars, because a period that has ended no longer changes. The first query, the first one of a new period, and one after a change of the series, the bucket, the time zone or the recorded coverage, or to a longer range, read every recorded sample of the range, so a reading that reaches the history stores late shows in its bar from the next period on at the latest. A changed series, range or bucket queries at once. After the first query of a chart, and after one at once, it queries once more one full window later, because the history stores may not have held the latest values yet. A chart that nobody views costs nothing.
- **No history**: A series whose property is not recorded shows "no history recorded" in the legend, and a path that leads nowhere shows "not found". Without any history store every series shows "no history store". None of these is an error. Only `[State]` properties of a number, a boolean, an enum or a text are recorded.
- **Aggregation**: A line whose aggregation the property or the history stores do not offer shows the automatic one, with a note in the legend that names both, for example "(Sum not available, shows Last)" for a counter. This is no error, because stores offer different aggregations. `Minimum`, `Maximum`, `Sum`, `SampleAverage`, `StandardDeviation` and `First` read only the samples recorded inside a bucket and ignore the value held into it: a bucket without a recorded sample is a gap in the line, and the maximum of a bucket can be below its time-weighted average. `Count` is 0 for such a bucket. `Last` and the time-weighted average carry the value held into a bucket, see [Query semantics](../../architecture/design/history.md#query-semantics).
- **Errors**: An unknown `Range`, `Bucket` or `Aggregation`, a fixed `Bucket` too small for the range, a `Height` of 0 or less, a `RefreshInterval` longer than a day, or a line with another unit, shows a small red alert in place of the chart and its legend, see [Value Widget Errors](../page-errors.md#value-widget-errors). A query that fails shows the warning `Could not load history` with the reason in place of the chart, the legend stays, and the chart tries again with the next refresh.
- **History dialog**: A click on a line or a bar opens the history dialog of its series, a click beside them the dialog of the first line, and a click on an entry of the legend or in the header of a band the dialog of that series. The dialog shows the same chart for that one property with its axes, with a choice of range, bucket and aggregation, and opens with the range, bucket and aggregation of the chart. It offers `Delta` for a counter and draws it as bars, like the chart. A band opens it with the range of the chart and its recorded states. The lines open it with a mouse click only; with the keyboard, the entries of the legend and of the band headers are buttons that open the same dialogs. The history link of a property in the browser opens the same dialog. In edit mode a click selects the chart instead.

## Related

- [Basics](basics.md) - Type names, paths and colors
- [Overview](overview.md) - All widgets by group
- [History design](../../architecture/design/history.md) - How history is recorded and queried
