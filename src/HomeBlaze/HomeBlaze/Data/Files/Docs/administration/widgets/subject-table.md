---
title: SubjectTable
navTitle: SubjectTable
position: 36
---

# SubjectTable

A table with one row per subject and one column per property, for example all motors of a folder with their speed and temperature. Type name: `HomeBlaze.Components.SubjectTable`. Slot blocks: `Column`.

## Arguments

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `Source` | Path of a subject, or of a property that holds subjects | None | Where the rows come from, see Behavior and Limits. The picker offers subjects; a path to a property is typed |
| `Type` | Text | None | The full name of a class, a base class or an interface, for example `HomeBlaze.Samples.Motor`. Only subjects of that type are rows |

## Slots

| Slot | Blocks | Holds |
|------|--------|-------|
| `Column` | Any number | One column per block, from left to right. It holds no content |

The arguments of a `Column` block:

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `Property` | Text | None | The name of a property of the row subject, for example `CurrentSpeed`. It is a name, not a path |
| `Label` | Text | From the property, see [Labels](basics.md#labels) | The header of the column |
| `Map` | Name or path of a `ValueMap` | None | The map that gives text, color and icon for a value, see [ValueMap](value-map.md) |

## Samples

The sample motors of this folder. `..` is the folder of this page, and the type keeps the motors and leaves out the pages. Without columns the table shows the name of each subject:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.SubjectTable Source=".." Type=HomeBlaze.Samples.Motor
::
~~~~

:: HomeBlaze.Components.SubjectTable Source=".." Type=HomeBlaze.Samples.Motor
::

The same motors with columns. The status column uses a map on the same page:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.ValueMap#tablestatus
::: Case Value=Running Color=Success
::: Case Value=Stopped Color=Error
:: HomeBlaze.Components.SubjectTable Source=".." Type=HomeBlaze.Samples.Motor
::: Column Property=Name Label="Motor"
::: Column Property=Status Map=tablestatus
::: Column Property=CurrentSpeed Label="Speed"
::: Column Property=Temperature
::
~~~~

:: HomeBlaze.Components.ValueMap#tablestatus
::: Case Value=Running Color=Success
::: Case Value=Stopped Color=Error
:: HomeBlaze.Components.SubjectTable Source=".." Type=HomeBlaze.Samples.Motor
::: Column Property=Name Label="Motor"
::: Column Property=Status Map=tablestatus
::: Column Property=CurrentSpeed Label="Speed"
::: Column Property=Temperature
::

An interface as type: every subject in the folder that measures a temperature, whatever its class is:

<!-- sample: live -->
~~~~markdown
:: HomeBlaze.Components.SubjectTable Source=".." Type=HomeBlaze.Abstractions.Sensors.ITemperatureSensor
::: Column Property=Name
::: Column Property=Temperature
::
~~~~

:: HomeBlaze.Components.SubjectTable Source=".." Type=HomeBlaze.Abstractions.Sensors.ITemperatureSensor
::: Column Property=Name
::: Column Property=Temperature
::

## Behavior and Limits

- **The rows**: A `Source` that leads to a property that holds subjects (one subject, a list or a dictionary) gives the subjects of that property, for example `Source=tablestatus/Cases` for the cases of a map. A `Source` that leads to a subject gives the subjects that it holds as a folder: the files and folders of a folder of the storage, or the blocks of a page. Any other `Source` shows `Cannot list subjects of` with the path in place of the table, and a table without `Source` shows `Needs a Source`, see [Value Widget Errors](../page-errors.md#value-widget-errors).
- **The order**: The rows are in the order in which the source holds them.
- **The type**: `Type` is compared with the full name of the class of a row, of each of its base classes and of each of its interfaces, case-sensitive. A generic class or interface also matches the full name of its definition, like ``Namespace.IDevice`1``. A short name like `Motor` matches nothing. Without `Type` every subject is a row, pages and folders included.
- **The cells**: A cell shows the value of the property of its row like every [value widget](basics.md#value-widgets): formatted with its unit, with the text and color of a map.
- **A missing value**: A cell shows the dash of a [missing value](basics.md#missing-values) also when its row has no such property, with the name of the property as tooltip. A cell of the first column shows the title of the subject in its place.
- **The link**: The cell of the first column links to the subject in the subject browser. A table without columns has one column `Name` with the title of each subject, as a link.
- **Live rows**: The rows follow when subjects are added to or removed from the source.
- **Limits**: There is no sorting, no paging and no editing in the table.

## Related

- [Basics](basics.md) - Paths, value widgets, missing values and colors
- [Overview](overview.md) - All widgets by group
- [Gauge](gauge.md) - One number of one subject as a ring
