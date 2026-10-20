---
title: Metadata
navTitle: Metadata
---

# Metadata

Metadata is keys and JSON values that describe a folder, a subject or a property, such as its title in the navigation, its icon, the order of its children, its notes, links and tags or a comment. A storage keeps it in `.metadata.json` files, apart from the configuration of the subjects. People change it in the browser or by hand, and agents through methods of the storage. Pages keep their title, icon and place in the navigation in their [frontmatter](pages.md#frontmatter) instead. The design is in [Metadata Design](../architecture/design/metadata.md).

On this page:

- [Where Metadata Is Stored](#where-metadata-is-stored): the files of folders, subjects and the root
- [Folders](#folders): the title, the icon and the order of a folder
- [Navigation Order](#navigation-order): the order of pages, folders and subjects, the top bar and reordering in the browser
- [The Metadata Dialog](#the-metadata-dialog): the keys that the browser edits
- [Notes, Links and Tags](#notes-links-and-tags): their keys and rules, how the browser shows them and the list of everything with a tag
- [File Format](#file-format): the keys, the paths of subjects and properties, and the rules and limits of the files
- [Agents](#agents): reading, finding and changing metadata and order through the MCP tools
- [Related Documentation](#related-documentation)

---

## Where Metadata Is Stored

| Item | File |
|------|------|
| A folder | `.metadata.json` inside it |
| A subject stored as `X.json`, every subject inside it and their properties | `X.metadata.json` beside it |
| The root of the storage | `.metadata.json` in the data folder |
| A page | Its frontmatter, see [Markdown Pages](pages.md#frontmatter) |

The `.metadata.json` in the data folder itself holds the `order` of the top level and the metadata of the root of the storage.

---

## Folders

A folder keeps its title, its icon and the order of its children in a file named `.metadata.json` inside it:

```json
{
  "title": "All Devices",
  "navTitle": "Devices",
  "icon": "Devices",
  "order": ["Readme.md", "Heat Pump", "Pumps"]
}
```

| Key | Description |
|-----|-------------|
| `title` | Title of the folder everywhere. Defaults to the name of the directory |
| `navTitle` | Title of the folder in the sidebar. Defaults to `title` |
| `icon` | MudBlazor icon name of the folder. Defaults to `Folder` |
| `order` | Names of the children in the order they are shown |

---

## Navigation Order

Pages, folders and subjects appear in the same order in the sidebar, the top bar and the browser. A folder keeps that order in the `order` of its `.metadata.json`.

A name in `order` is the name of a child as the browser shows it in its address: a subject file without `.json` (`Motor` for `Motor.json`), any other file with its extension (`Readme.md`) and a folder by its name. Children that are not listed follow the listed ones: `readme` first, `index` second, then by navigation title. A name of a child that no longer exists is ignored. A JSON file that does not load as a subject, for example while its JSON is invalid, is listed under its file name with `.json`, so its entry does not match it until it loads again, and it is shown among the children that are not listed meanwhile.

### The Top Bar

The top bar collects its items from the whole tree, not only from the top level: every page with `location: AppBar`, and every subject with a page whose `X.metadata.json` sets `location` to `AppBar`, appears there, in whichever folder it is. The items follow the navigation order: the folders are walked in their order, and the items inside a folder come in the order of that folder. **Reorder** in the folder of an item moves it among the items of that folder, and the place of the folder in its parent moves its items with it. Each item goes to the left or the right of the top bar by its `alignment`, set in the frontmatter of a page or in the **Metadata** dialog of a subject, and each side keeps the navigation order. The sidebar shows a folder only when it holds a page for the sidebar, reached through folders and subjects that are in the sidebar themselves, so a folder whose pages are all in the top bar does not appear there, and neither does a page that can only be reached through an item of the top bar. A folder that is a page itself and holds no page for the sidebar shows as a link to its page.

### Ordering in the Browser

**Reorder** in the header of a folder with at least two children switches its list of children to reorder mode, where a row moves by dragging it, by its handle on a touch screen, or by one place with its up and down arrows, which Tab reaches from the keyboard; **Done** switches back. Reordering is disabled while the storage is not connected. Each move writes the whole order of the folder to its `.metadata.json`, so after the first move every child has its place, and a child added later comes after them until it is moved.

---

## The Metadata Dialog

The **Metadata** button in the header of the browser opens a dialog for every item whose metadata a storage keeps: a folder, a subject stored as `X.json` and every subject inside one, at any depth, and a storage stored as a JSON file inside another storage. It is colored when the item has metadata of its own. An item with notes, links or tags always has the button, which first shows them, see [Notes, Links and Tags](#notes-links-and-tags). Otherwise the button is not shown for the root of the storage, whose `.metadata.json` is edited by hand or by agents, nor for pages and the subjects of their blocks, and it is disabled while the storage is not connected. For a folder it sets `title`, `navTitle` and `icon`; for a subject `navTitle`, `icon`, `location` and `alignment`; for both the [notes, links and tags](#notes-links-and-tags):

| Key | Description |
|-----|-------------|
| `navTitle` | Title in the navigation |
| `icon` | Icon in the navigation. The icon of the subject itself is unchanged |
| `location` | `NavBar` or `AppBar`, for a subject that has a page |
| `alignment` | `Left` or `Right` in the top bar |

A save writes only the keys whose fields were changed: a field left empty removes its key, and every other key stays as it is, also one that was written while the dialog was open, and one whose value the dialog does not show as it is held, such as a number, or links or tags that break the [rules](#notes-links-and-tags), edited by hand. A changed key is written as the dialog shows it, replacing what was written meanwhile, and a change to any link writes all links as the dialog shows them, without those it could not read, such as a link edited by hand without a url or path, and without members other than `title`, `url` and `path`. Metadata never changes the title or the icon of the subject itself. A subject that is a page itself decides its place in navigation, so it does not use `location` and `alignment`. Pages keep their metadata in their frontmatter, which the dialog does not change.

---

## Notes, Links and Tags

Every folder, subject and property can have notes, links and tags; pages and other files cannot have them yet, which is planned. The pane shows the tags of its folder or subject below its header. When the folder or subject has notes, links or tags, the [Metadata button](#the-metadata-dialog) shows them in a dialog, where its links and tags can be opened, and whose **Edit** button opens the **Metadata** dialog, except on the root of the storage, where the dialog has no **Edit**. The row of a property with notes, links or tags shows an icon with them in a tooltip, also when the property has no value. A click on the icon shows them in a dialog, where its links and tags can be opened, and its **Edit** button opens the **Metadata** dialog for that property, which edits its notes, links and tags. On the row of a property without them, the icon shows while the row is hovered, and a click opens the **Metadata** dialog. While the storage is not connected, the tooltip and the dialogs still show them, and **Edit** shows a message instead of the **Metadata** dialog. A property whose metadata no storage keeps, such as one of the root of the storage, shows its icon, tooltip and dialog without **Edit** when it has notes, links or tags, and cannot be edited in the browser.

| Key | Description |
|-----|-------------|
| `notes` | Markdown of up to 4 KB in UTF-8, shown formatted. It may not hold HTML, pictures, blocks, attributes, such as `{.class}`, `{#id}` or `{key=value}`, or `{{ }}` expressions, and a refused write names the rule it breaks. Braces with only names, such as `{low}`, and footnotes are shown as text. Its links only lead to http, https or mailto addresses or to the routes of HomeBlaze: `/`, the browser, such as `/browser/Devices/Pump`, and pages, such as `/pages/Docs/Setup.md`; not to a relative path, a path with `.` or `..` segments, a `#` fragment or any other path. A link to an http or https address opens in a new tab without referrer. Headings get no identifiers. Notes that break these rules, for example after an edit by hand, are shown as text, at most their first 4 KB. The dialog stores notes as typed, and blank notes remove the key |
| `links` | Up to 20 links, each with an optional `title` and either a `url`, an http, https or mailto address, or a `path`, the absolute path of a page, subject or property, such as `/Devices/Pump/Temperature`, all without control or format characters, such as bidirectional overrides or zero-width characters. In the dialog, an address that starts with `/` is a path, which the path picker helps to find. An http or https link opens in a new tab, which its icon shows, and a mailto link in place. A link to a page opens the page, to a subject the subject in the browser, and to a property its subject with the row of the property highlighted. A path that leads to nothing or to a property that the browser does not show, and an address other than http, https or mailto, for example after an edit by hand, shows as a broken link that cannot be opened. Without a title, a link shows what it leads to, and so does a link whose title, edited by hand, has control or format characters; a link whose url or path, edited by hand, has them is not shown. Of a file edited by hand, the first 20 links are read, with their members trimmed |
| `tags` | Up to 20 tags of 1 to 64 characters without control or format characters, each given once without regard to case. The dialog suggests the tags in use that contain the typed text: Enter adds the typed tag, and choosing a suggestion adds that tag. A click on a tag in the pane lists everything that has it, without the subjects that the browser does not list, those excluded from user interfaces, and what is below them, and without the properties that the browser does not show. Of a file edited by hand, the first 20 tags are read, trimmed and each once, without those that break these rules |

```json
{
  "notes": "Calibrated **2026**, see the [manual](https://example.com/pump.pdf).",
  "links": [
    { "title": "Manual", "url": "https://example.com/pump.pdf" },
    { "title": "Flow temperature", "path": "/Devices/HeatPump/FlowTemperature" }
  ],
  "tags": ["floor-2", "heating"]
}
```

---

## File Format

Keys are matched exactly, case included, so `NavTitle` is a key of its own that HomeBlaze does not read as `navTitle`, and a save that sets a known key in another case is refused with the name of the known key. The file may have comments and a trailing comma after its last value. File names are matched exactly: the file of a folder is `.metadata.json` and the file beside `X.json` is `X.metadata.json`, with `.metadata.json` in lower case. Keys that HomeBlaze does not know, such as `comment`, are kept when HomeBlaze writes the file. HomeBlaze writes the file indented and without comments, with characters such as `ü` as they are and characters outside the basic multilingual plane, such as emoji, as escapes like `\uD83D\uDE00`, and deletes it once no key has a value.

### Subjects and Properties

The metadata of a subject stored as `X.json`, of every subject inside it and of their properties is kept in `X.metadata.json` beside it, also when `X.json` loads as a plain JSON file. The keys at the top describe the subject of `X.json` itself, so a simple file looks like the file of a folder:

```json
{ "navTitle": "Pump", "icon": "WaterDrop" }
```

`$properties` holds an entry for each property with metadata, and `$subjects` one for each subject inside, by its path relative to the subject of `X.json`. A property of the subject of `X.json` itself is just its name:

```json
{
  "navTitle": "Heat Pump",
  "$properties": {
    "FlowTemperature": { "comment": "measured at the outlet" }
  }
}
```

### Paths

A path names the properties on the way, separated by `/`, with the index of a collection or the key of a dictionary in brackets, as `browse` shows it:

```json
{
  "navTitle": "Shelly Pro 4PM",
  "icon": "ElectricalServices",
  "$subjects": {
    "Channel": { "navTitle": "Pump relay" },
    "Channels[0]": { "navTitle": "Hall light" },
    "Meters['Main']/Phases[2]": { "area": "Utility room" }
  },
  "$properties": {
    "Firmware": { "comment": "auto-update disabled" },
    "Channels[0]/Power": { "comment": "includes the hall heater" }
  }
}
```

`Channel` in `$subjects` is the subject held by the property `Channel`, and in `$properties` the property itself. HomeBlaze writes paths in this notation and also reads the notation of browser addresses, such as `Channels/0/Power`. Two entries for the same subject or property are a mistake, of which the first one applies, and saving metadata of that subject or property is refused until one is removed. Keys that start with `$` are reserved. An entry whose subject or property does not exist is kept and applies once it exists.

### Collections and Moves

Items of a collection are matched by their index. When the items of a collection change places, each item keeps the metadata it has until the file is applied again, which gives the metadata of an index to the item now at that index. A subject that moves keeps the metadata of its previous place when it moves out of every storage or into a stored subject whose file was not applied, and when it is added to its new place before it is removed from the old one.

### Nested Storages

A storage that is stored as a JSON file inside another storage keeps all its metadata, its `order` included, in the `.metadata.json` in its own root. A sidecar beside its file in the other storage is not read.

### Limits

Saving metadata is refused beyond these limits: at most 64 keys per entry, key names of up to 64 characters, strings of up to 256 characters and values of up to 4 KB, except for the `order` of a folder and for `notes`, which have a limit of their own, and a file of up to 512 KB, which a save that does not make it larger may exceed. The limits are checked only on save, so a file edited by hand is read whatever its size. An entry without keys is removed, and a file without keys is deleted.

### Reserved Names

A file whose name ends with `.metadata.json` in lower case is never loaded as a subject, and one that holds a `$type`, such as a subject file that was named so before, is logged with a warning. A file whose name ends in another case, such as `Pump.Metadata.json`, is neither a subject nor a metadata file, and is logged with a warning to rename it in lower case. A subject cannot be named `Something.metadata` in any case, as on a file system that ignores case its file would be the metadata file of `Something.json`. Deleting a subject also deletes its `X.metadata.json` sidecar. A sidecar of a file that is moved or renamed outside HomeBlaze has to be moved or renamed with it.

### Files That Cannot Be Read

A metadata file that cannot be read, because its JSON is invalid or one of its keys cannot be read, is ignored with a warning: the folder or subject then shows no metadata, and saving metadata or moving a child is refused with an error until the file is fixed or deleted by hand. Every save reads the file again first, so a change made by hand that HomeBlaze has not loaded yet is kept.

### Bundled Documentation

When a [seed directory](configuration.md#homeblazeseeddirectory) is set, the documentation under `/Docs` is replaced with the bundled documentation on every start, so changes to its order or metadata are lost on the next start.

---

## Agents

Agents read metadata with the MCP tools: `browse` and `search` show the metadata of a subject as its property `HomeBlaze:Metadata` with `includeProperties`, and the metadata of a property as its attribute `@HomeBlaze:Metadata` with `includeAttributes`, both only where there is metadata. `get_property` reads `/Devices/Shelly/HomeBlaze:Metadata` and `/Devices/Shelly/Firmware@HomeBlaze:Metadata`. `set_property` cannot change metadata; agents change it with these methods through `invoke_operation` and `invoke_query`, when the agent may use these tools. The browser does not show the methods:

| Method | On | Description |
|--------|----|-------------|
| `SetMetadata` | a storage, such as the root `/` | Takes `path`, the path of a subject relative to the storage, such as `Devices/Shelly/Channels[0]` (empty for the storage itself, a leading `/` ignored), an optional `property`, the name of one of its properties, and `values`, a JSON object that is merged into the metadata: a key with a value is set, a nested value replacing the old one as a whole, a key with `null` is removed, and every other key stays |
| `GetOrder` | a folder, a storage | Returns the names of the children in the order in which they are shown. These names, like those that the message of a refused order lists, are names of children and not secret, so they include children that are hidden from agents |
| `SetOrder` | a folder, a storage | Takes `names` and stores them first, in that order, then the other children in the order in which they are shown. A name that is not a child, or that is given twice, is refused |
| `SearchMetadata` | a storage | Takes `tag`, `key`, `value` and `path`, each optional, and returns the subjects and properties whose metadata matches all that are given: a tag in `tags`; a key that is set, such as `notes`; a value that equals the whole of a string, or of a string in an array, of `key` when it is given, else of any key, and never a number or a boolean; and a `path` relative to the storage at or below which they are. Keys match exactly, and tags and values without regard to case. Each match has `path`, the path of its subject relative to the storage, and `property`, omitted for a subject, as `SetMetadata` takes them. The matches are sorted by path and then property. The MCP tools return at most the first 100 and never one of a subject or property that they do not show, and to an agent never one that `SetMetadata` would refuse it, such as the metadata of an agent |

`SetMetadata` checks the keys HomeBlaze knows: `title`, `navTitle` and `icon` take a string, and a blank string removes the key; `location` takes the name `NavBar` or `AppBar` and `alignment` the name `Left` or `Right`; `notes`, `links` and `tags` follow the rules of [Notes, Links and Tags](#notes-links-and-tags), and an empty array or a blank string removes the key; `order`, which only a folder or a storage has, takes an array of names, which `SetOrder` checks against the children. Other keys are stored as given, within the [limits](#limits). A refused call returns its reason to the agent, such as a file that could not be read or a storage that is not connected. Agents cannot change the metadata of an agent or of a subject inside one.

```json
{ "navTitle": "Hall light", "icon": null }
```

To find text inside notes, agents use `search_markdown`, which also searches the notes of metadata and marks each such match with `kind: notes`, the path of its subject and, for the notes of a property, `property`, the name of the property. It does not search the notes of subjects of a type excluded from agents, nor of what is below them, and for agents not the notes of an agent or of a subject inside one.

Which tools an agent may use is set in [AI Agents](ai-agents.md#tools-and-permissions).

---

## Related Documentation

- [Markdown Pages](pages.md) - Frontmatter of pages, which keeps their title, icon and place in the navigation
- [Subjects, Storage & Files](subjects.md) - Subject files and the layout of the data folder
- [AI Agents](ai-agents.md) - Tools and permissions of agents
- [Metadata Design](../architecture/design/metadata.md) - How metadata is held, applied and written
