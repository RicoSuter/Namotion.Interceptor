---
title: Virtual Subjects
navTitle: Virtual Subjects
---

# Virtual Subjects

A virtual subject has properties that come from other subjects instead of a device. Each property is either an **expression**, which is read-only and is calculated again whenever a value it reads changes, or a **binding**, which reads another property and writes through to it. Use one to gather the values of a few devices in one place, to compute a value such as a share or a sum, or to give a value a name and a unit of your own.

## Quick start

Create a subject of the type **Virtual subject**, add a property, and give it an expression such as `/Devices/Inverter/Power / /Devices/Meter/Power`. The value shows as soon as the subject is saved. Choose **Binding** instead to read and write a property of another subject under a new name. The file `Demo/PumpOverview` in the development data shows a binding and an expression.

## The editor

A virtual subject is edited in the subject edit dialog, the pencil on the subject panel in the Browser. The dialog shows a tree of the properties, nested subjects and attributes on the left and the details of the selected entry on the right. The first node of the tree is the subject itself, and selecting it adds new top-level entries. An entry is a **Binding** or an **Expression**. For a binding, a path picker chooses the target, and **Use type and unit** takes over the data type and unit of the target once, makes the entry state, and takes the title of the target when the entry has none. The **State** checkbox makes a property state, and it shows the state fields (title, unit, position and so on). Entries with issues are marked, and **Save** is disabled while there are issues.

## The configuration

A virtual subject is stored as a JSON file. Its `properties` are a tree in which each entry is exactly one of these:

| Entry | Has | Becomes |
|---|---|---|
| Expression property | `expression` and `dataType` | A read-only property, calculated again when a value it reads changes |
| Binding property | `path` and `dataType` | A property that reads the target and writes through to it |
| Nested subject | `properties` | A subject with entries of its own |

An expression or binding property can also have `state` and `attributes`:

```json
{
  "$type": "HomeBlaze.Virtual.VirtualSubject",
  "title": "Solar",
  "properties": {
    "Pv": {
      "properties": {
        "Power": {
          "path": "/Devices/Inverter/Power",
          "dataType": "Number",
          "state": { "title": "PV power", "unit": "Watt" },
          "attributes": {
            "Maximum": { "expression": "10000", "dataType": "Number" }
          }
        },
        "Share": {
          "expression": "Power / /Devices/Meter/Power",
          "dataType": "Number",
          "state": { "title": "PV share", "unit": "Percent" }
        }
      }
    }
  }
}
```

The properties of this subject are `Solar/Pv/Power`, `Solar/Pv/Share` and the attribute `Solar/Pv/Power@Maximum`. There is one way to write each thing: there are no shorthands. The unit `Percent` is a fraction from 0 to 1 that HomeBlaze shows times 100, so `Share` does not multiply by 100 itself.

## Data types

| `dataType` | Value |
|---|---|
| `Number` | A decimal number. A value that is no number, or one beyond the range of a decimal, is no value |
| `Boolean` | True or false |
| `Text` | Text; the value of an enumeration is its name |
| `Duration` | A duration |
| `DateTime` | A point in time |

## Paths

Expressions use the syntax of [page expressions](pages.md#live-expressions) without pipes and variables. A path without `/` names a property of the same subject, such as `Power` in the example. `./` and a path with `/` are relative to that subject, `../` goes up one level, and a leading `/` starts at the root. After a comparison operator a bare word is text, as in page expressions, so compare with a property of the same subject as `./Limit`, and put spaces around `/` when dividing, as in `Power / Limit`; see [Conditions](pages.md#conditions) and the [expression reference](https://github.com/RicoSuter/Namotion.Interceptor/blob/master/docs/expressions.md). A secret and a property excluded from expressions cannot be read. A property that appears later, such as one of a device that loads later or one added at run time, is picked up automatically.

## State and helpers

A property with `state` is state: it is shown in the UI, recorded in history and exposed over OPC UA and MCP. A property without `state` is a helper: it is hidden, but other expressions of the subject can read it.

## Attributes

`attributes` holds registry attributes of a property, such as `Minimum`, `Maximum` or `IsEnabled`. An attribute is defined like a property, with an expression or a binding and a data type, and can have attributes of its own. A constant is an expression such as `"expression": "0"`.

## Errors

An entry that is not valid is left out, and the others apply. The status of the subject is then **Error**, its status message names each entry that was left out and why, and the same list is logged as one warning. A path that leads nowhere gives no value, and the value appears when the path leads to a property.

A write to a binding is rejected when the target is missing, has no public setter or holds subjects, or when the value does not convert to the type of the target. A value with a fraction is rejected by a whole-number property instead of being rounded, and an enumeration takes a name only: a number or a name the enumeration does not define is rejected.

## Limitations

- **A change of the properties creates the subject again.** The registry cannot remove a property yet, so a saved change of `properties` replaces the subject with a new instance at the same path. Pages, automations and history follow it by its path.
- **A binding does not take over the attributes of its target.** Its `Minimum`, `Maximum` and `IsEnabled`, and its title and unit, are not copied. Declare the attributes and the state you need. The editor offers to take over the data type and unit once, when you pick the target; see [The editor](#the-editor).
- **Bindings reach scalar values only.** A binding cannot target a property that holds subjects. It writes only to a property that has a public setter.
- **No time in expressions.** Expressions have no variables such as `now`, so a value based on the current time would not be calculated again.
- **A change high in the tree recalculates more.** A virtual property is calculated again when a subject on the way of one of its paths is added, removed or replaced. A change near the root, such as a file added at the top level, therefore recalculates every virtual property whose paths pass through the root.
- **Properties that read each other are rejected.** Within one virtual subject, entries that read each other, such as `A` reading `B` and `B` reading `A`, also through absolute paths that start with the path of the subject itself, are left out and reported in the status. Across two virtual subjects the cycle is not detected: the properties give no value, and in rare cases two of them recalculated at the same time on different threads can block each other (#971). Avoid virtual subjects that read each other.
- **Store virtual subjects in a storage.** A virtual subject in the root configuration file takes a change of its properties only after a restart.
- **Only virtual subjects have virtual properties.** Adding computed properties to an existing subject, such as a device, needs the registry to remove properties first (#964).
