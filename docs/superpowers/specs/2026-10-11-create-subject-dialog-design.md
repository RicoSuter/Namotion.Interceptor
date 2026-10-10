# Create Subject Dialog Redesign

## Problem

The dialog that creates a new subject (`SubjectSetupDialog`) is hard to use and has several defects. It is opened from three places: the "Create" operation of a storage folder, the embed button of the markdown editor, and the "add" action of the grid and canvas layouts.

Usability problems:

- The name is asked first, before the user has chosen what to create.
- There is no search. All types are shown as tall cards in an unsorted list of unsorted groups.
- Titles are raw class names such as `GpioSubject` and `InMemoryHistoryStoreSubject`, and descriptions are rendered in the theme's pink secondary color.
- Types that must not be created standalone are offered: `GridCell`, `CanvasNode` and `PluginManager` appear under "Uncategorized" with "No description".
- The configure page shows the subject's own "Name" setting right after the dialog asked for a "Name" on the first page. The first is a display name, the second is the file name, and nothing explains the difference.

Defects:

- Selecting a type before entering a name gives no feedback. Only a disabled "Next" button appears.
- The "Configure" tab header is clickable at any time and leads to an empty page with a stale title.
- "Create" is enabled while the configure form is invalid. `SubjectEditPanel` learns its validity after its first render and does not notify the dialog.
- After "Back", picking a different type keeps the subject created for the first type, because the subject is assigned with `??=`.
- The grid and canvas callers discard the name, yet the dialog requires a valid file name there.
- A name that already exists in the folder is accepted, and the storage then overwrites the existing file.

## Goals

- Pick the type first, with search and a group filter.
- Ask for the name on the configure page, prefilled with a suggestion, and state clearly that it becomes part of the subject's ID.
- Do not offer types that must not be created standalone.
- Fix the defects listed above.

## Non-Goals

- Selecting several groups at once. Search covers that need.
- Renaming existing subjects.
- Changes to the individual setup and edit components of the subject types.
- Refreshing the type registry when a plugin is loaded at runtime.
- Per-caller type filters. Hiding types without a widget component from the grid and canvas was considered and dropped: the regression test for hosted subjects created inside a grid adds a history store to a cell, which is such a type, so that path is supported and stays reachable.

## User Experience

### Page 1: "Create subject"

- A search field sits at the top and is focused when the dialog opens. It matches display name, class name, description and group, case-insensitively, and always searches across all groups.
- A sidebar lists "All" (the default) followed by the groups in alphabetical order, with "Other" last. Exactly one entry is selected. Each entry shows a count that follows the current search.
- The list shows one row per type with icon, display name and the full description in the normal secondary text color. Rows are sorted by display name. Under "All", rows are grouped under small group headers.
- Clicking a row goes straight to page 2. There are no tabs and no "Next" button.
- When nothing matches, the list shows "No types match" and a button that clears the search.
- At phone width the sidebar becomes a single row of group chips above the list.

### Page 2: "Create {display name}"

- A back arrow returns to page 1 with the search text and the selected group preserved. Picking a different type afterwards creates a fresh subject and a fresh form.
- The name field comes first, when the caller wants a name. It is prefilled with a suggestion.
- Below it, the type's setup or edit component is shown unchanged.
- "Create" is disabled until the name and the form are both valid.
- "Cancel" closes the dialog. The subject that was created for the form is never attached and is simply dropped.

### Name field

- Label "File name", with a fixed `.json` suffix shown at the end of the field.
- Helper text: "Part of the subject's path, which is its ID. Avoid renaming it later."
- Suggestion: the type name in PascalCase, for example `ShellyDevice`. When that name is taken in the target, a number is appended starting at 2 (`ShellyDevice2`, `ShellyDevice3`). The comparison is case-insensitive.
- Validation: required, no characters that are invalid in a file name, and not already used in the target.

### Differences per caller

| Caller | Name field |
|--------|-----------|
| Storage folder "Create" | "File name" with `.json` suffix, existing names are the folder's children |
| Markdown editor embed | "Name" without suffix, becomes the `subject(Name)` block name, existing names are the page's other subject blocks |
| Grid and canvas "add" | not shown |

## Type Metadata

The picker reads its information from attributes on the subject type. All four have a fallback, so a type from a plugin that sets none of them is still listed and readable.

| Attribute | Source | Purpose | Fallback |
|-----------|--------|---------|----------|
| `[DisplayName("Shelly device")]` | `System.ComponentModel` | Title in the picker and on page 2 | Class name split into words |
| `[Description("...")]` | `System.ComponentModel`, already used | Description in the picker | No description line |
| `[Category("Devices")]` | `System.ComponentModel`, already used | Group | "Other" |
| `[SubjectIcon("Hub")]` | new, `HomeBlaze.Abstractions.Attributes` | Icon in the picker, using the same names as `IIconProvider.IconName` | Default icon of the group |

Two more new attributes in `HomeBlaze.Abstractions.Attributes` control whether a type is offered:

- `[ExcludeFromCreation]`: the type is never offered. Applied to `GridCell` and `CanvasNode`.
- `[SingleInstance]`: the type is offered only while no instance of it exists in the subject graph. Applied to `PluginManager`.

Every type the dialog offers today gets a `[DisplayName]` and a `[SubjectIcon]`. `FluentStorageContainer` additionally gets `[Category("Storage")]` and a description, because creating one is a legitimate way to mount another storage.

`IIconProvider` stays as it is. It describes a live instance and often depends on state, which is why the picker cannot use it.

## Architecture

The dialog is split into three units with one purpose each.

### `SubjectTypeCatalog` (`HomeBlaze.Services`)

Plain C#, no UI dependency.

- Builds the list of creatable types from `SubjectTypeRegistry.RegisteredTypes`: types that implement `IConfigurable`, minus `[ExcludeFromCreation]`, minus `[SingleInstance]` types that already have an instance in the subject graph.
- Maps each type to a `SubjectTypeDescriptor` record with the type, display name, description, group and icon name, applying the fallbacks.
- Sorts groups alphabetically with "Other" last, and types by display name.
- Filters descriptors by search text and group, and returns the count per group for the current search.
- Suggests a name for a type given the names that already exist.

The list is built each time the dialog opens. Nothing is cached across dialogs, which removes the stale cache noted in the current code.

### `SubjectTypePicker.razor` (`HomeBlaze.Components`)

Renders the search field, the sidebar and the list. It receives the descriptors and raises an event when a type is selected. It holds the search text and the selected group as its own state and contains no creation logic.

### `SubjectSetupDialog.razor` (`HomeBlaze.Components`)

Hosts the picker and the configure page, and owns the name field, the back navigation and the create action. The order of the create action stays as it is today: apply the edits, invoke the caller's `BeforeSave`, persist, close.

### Caller contract

`SubjectSetupDialog.ShowAsync` takes one options object that replaces the bare `beforeSave` parameter:

- `BeforeSave`: unchanged meaning.
- `Name`: null when the caller wants no name, otherwise the label, the suffix and the existing names.

`ISubjectSetupService.CreateSubjectAsync` receives a request carrying the existing names, so that `VirtualFolder` and `FluentStorageContainer` can pass the keys of their `Children`. `CreateSubjectResult.Name` becomes nullable for callers that ask for no name.

### Other changes

- `SubjectEditPanel` gets an `IsValidChanged` callback, raised whenever the validity of the hosted component or of the properties editor changes, so the dialog re-renders its "Create" button.
- The icon name resolver (`ResolveMudBlazorIcon`) moves from `HomeBlaze.Host` to `HomeBlaze.Components`, because the picker cannot reference the host project. The host keeps using it from the new location.
- The per-keystroke allocation in the file name check is removed by keeping the invalid characters in a static set.

## Error Handling

- A name collision is caught by the field validation, before "Create" is enabled.
- When applying the edits reports invalid input, the dialog stays open on page 2.
- When `BeforeSave` or persisting throws, the existing error snackbar is shown and the dialog stays open. For the storage callers a failing `AddSubjectAsync` happens after the dialog has closed and is reported as it is today.
- A file that another process creates between validation and create is not detected by the dialog. The storage reports it.

## Testing

Unit tests for `SubjectTypeCatalog` (`HomeBlaze.Services.Tests`):

- `[ExcludeFromCreation]` types are not listed.
- A `[SingleInstance]` type is listed without an instance and not listed with one.
- Fallbacks for display name, group and icon.
- Group order with "Other" last, and type order by display name.
- Search over display name, class name, description and group, and the per-group counts.
- Name suggestion in PascalCase, numbering on collision, case-insensitive comparison.

End-to-end tests (`HomeBlaze.E2E.Tests`, `SubjectSetupDialogTests` rewritten for the new flow):

- The dialog opens on the picker with the search focused and no name field.
- Search narrows the list and updates the sidebar counts. A search without matches shows the empty state.
- Selecting a group shows only that group.
- Picking a type opens page 2 with the suggested name.
- "Back" preserves the search, and picking another type shows that type's form.
- "Create" is disabled while a required field is empty.
- A name that already exists in the folder is rejected.
- A successful create adds the subject to the folder under the chosen name.
- The grid "add" dialog shows no name field, and a hosted subject created there with its "Enabled" setting cleared still starts disabled.

No component-level tests are added. The repository has no bUnit, and the picker is a thin view over the catalog.

## Documentation

The HomeBlaze documentation is updated as part of the implementation, written as clean documentation of the result and not as a copy of this spec:

- `development/building-subjects.md` documents the four attributes, their fallbacks, and how a type is offered or hidden in the create dialog. Its existing mention of the setup wizard is brought in line with the new flow.
- `architecture/design/testing.md` is updated where it refers to the dialog's tests.
