---
title: Metadata
navTitle: Metadata
---

# Metadata

Metadata describes a folder, a subject or a property: its title and icon in the navigation, the order of the children of a folder, its notes, links and tags, or keys of your own such as a comment. A storage keeps it in `.metadata.json` files, apart from the configuration of the subjects. You change it in the browser or by hand, and agents change it through methods of the storage. Pages keep their title, icon and place in the navigation in their [frontmatter](pages.md#frontmatter) instead.

On this page:

- [Where Metadata Is Stored](#where-metadata-is-stored): the files of folders and subjects
- [Keys](#keys): the keys that HomeBlaze reads
- [Navigation Order](#navigation-order): the order of pages, folders and subjects, and the top bar
- [The Metadata Dialog](#the-metadata-dialog): editing metadata in the browser
- [Notes, Links and Tags](#notes-links-and-tags): their rules and how the browser shows them
- [File Format](#file-format): paths, rules and limits of the files
- [Agents](#agents): reading, finding and changing metadata through the MCP tools
- [Related Documentation](#related-documentation)

---

## Where Metadata Is Stored

| Item | File |
|------|------|
| A folder | `.metadata.json` inside it |
| The root of the storage | `.metadata.json` in the folder of the storage, `Files/` by default |
| A subject stored as `X.json`, the subjects inside it and their properties | `X.metadata.json` beside it |
| A page | Its frontmatter, see [Markdown Pages](pages.md#frontmatter) |

A folder file looks like this:

```json
{
  "title": "All Devices",
  "navTitle": "Devices",
  "icon": "Devices",
  "order": ["Readme.md", "Heat Pump", "Pumps"]
}
```

---

## Keys

| Key | Used by | Description |
|-----|---------|-------------|
| `title` | Folder | Title of the folder everywhere. Defaults to the name of the folder |
| `navTitle` | Folder, subject | Title in the navigation. Defaults to the title |
| `icon` | Folder, subject | MudBlazor icon name, such as `Dashboard`. A folder shows it everywhere and defaults to `Folder`; a subject shows it in the navigation only |
| `location` | Subject with a page | `NavBar` (sidebar, default) or `AppBar` (top bar) |
| `alignment` | Subject in the top bar | `Left` (default) or `Right` |
| `order` | Folder | Names of the children in the order they are shown, see [Navigation Order](#navigation-order) |
| `notes` | Folder, subject, property | Notes in Markdown, see [Notes, Links and Tags](#notes-links-and-tags) |
| `links` | Folder, subject, property | Links to web addresses, pages, subjects and properties |
| `tags` | Folder, subject, property | Tags that group items across folders |

Metadata never changes the title or the icon of the subject itself. Any other key, such as `comment`, is kept as it is.

---

## Navigation Order

Pages, folders and subjects appear in the same order in the sidebar, the top bar and the browser. A folder keeps that order in the `order` of its `.metadata.json`.

A name in `order` is the name of a child as the browser address shows it: a subject file without `.json` (`Motor` for `Motor.json`), any other file with its extension (`Readme.md`), and a folder by its name. Children that are not listed come after the listed ones: a readme first, an index second, then by navigation title. Names of children that do not exist are ignored. A JSON file that does not load as a subject, for example because its JSON is invalid, is named with `.json` until it loads again, so meanwhile it is sorted with the children that are not listed.

### Ordering in the Browser

**Reorder** in the header of a folder with at least two children switches its list of children to reorder mode. Drag a row, use its handle on a touch screen, or move it by one place with its up and down arrows, which Tab reaches from the keyboard. **Done** switches back. Reordering is disabled while the storage is not connected.

Each move writes the whole order of the folder, so every child has its place afterwards, and a child added later comes after them until it is moved.

### The Top Bar

The top bar shows every page with `location: AppBar` and every subject with a page whose metadata sets `location` to `AppBar`, in whichever folder it is. The items follow the navigation order: the folders in their order, and the items of a folder in the order of that folder. **Reorder** moves an item among the items of its folder, and moving a folder moves its items with it. Each item goes to the left or the right side by its `alignment`.

The sidebar shows a folder only when it holds a page for the sidebar, so a folder whose pages are all in the top bar does not appear there, and neither does a page that is reached only through an item of the top bar. A subject with a page of its own that holds no page for the sidebar shows as a link to its page.

---

## The Metadata Dialog

**Metadata** in the header of the browser opens a dialog for a folder, for a subject stored as `X.json` or inside one, and for a storage stored inside another storage. The button is colored when the item has metadata. A folder has the fields **Title**, **Navigation title** and **Icon**; a subject has **Navigation title**, **Icon**, **Location** and **Alignment**. Both have **Notes**, **Links** and **Tags**, see [Notes, Links and Tags](#notes-links-and-tags). The button is not shown for pages or for the subjects of their blocks.

When the item has notes, links or tags, the button first shows them, and **Edit** opens the dialog. Otherwise it opens the dialog directly and is disabled while the storage is not connected. While the storage is not connected, **Edit** shows a message instead. The root of the storage has the button only when it has notes, links or tags, and without **Edit**: its `.metadata.json` is changed by hand or by agents.

Saving writes only the fields you changed. A field left empty removes its key, and every other key stays, also one that changed while the dialog was open. Changing a link writes all links as the dialog shows them, so a link edited by hand that the dialog cannot read is removed.

---

## Notes, Links and Tags

Folders, subjects and properties can have notes, links and tags. Pages and other files cannot have them yet.

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

### In the Browser

The pane shows the tags of its folder or subject below its header, and the [Metadata button](#the-metadata-dialog) shows its notes, links and tags. The row of a property with notes, links or tags has an icon that shows them in a tooltip, also when the property has no value. A click on the icon shows them in a dialog with **Edit**. On the row of a property without them, an outlined icon appears while the row is hovered or has the keyboard focus, and a click opens the **Metadata** dialog. A property whose metadata no storage keeps, such as one of the root of the storage, can show its notes, links and tags but not edit them.

A click on a tag lists everything that has the tag, except what the browser does not show.

### Notes

Notes are Markdown of up to 4 KB, shown formatted. A save is refused, with the rule it breaks, when notes hold:

- HTML, pictures, blocks or `{{ }}` expressions
- Attributes, such as `{#id}`, `{.class}` or `{key=value}`
- A link that leads anywhere other than an http, https or mailto address or a route of HomeBlaze: `/`, the browser, such as `/browser/Devices/Pump`, or a page, such as `/pages/Docs/Setup.md`. A route may not contain `.` or `..` segments or backslashes, and relative paths and links to a `#` fragment alone are refused

Braces with only names, such as `{low}`, and footnotes are shown as text. A link to an http or https address opens in a new tab. Notes that break the rules, for example after an edit by hand, are shown as plain text.

### Links

A metadata entry has up to 20 links. Each link has an optional `title` and either a `url`, an http, https or mailto address, or a `path`, the absolute path of a page, subject or property, such as `/Devices/Pump/Temperature`. These member names are matched exactly, and a link has no other members. In the dialog, an address that starts with `/` is a path, and the path picker helps to find it.

A web link opens in a new tab and a mailto link in place. A link to a page opens the page, to a subject the subject in the browser, and to a property its subject with the row of the property highlighted. A link without a title shows what it leads to. A link that leads to nothing, to another kind of address or to a property that the browser does not show is shown as broken and cannot be opened.

### Tags

A metadata entry has up to 20 tags of 1 to 64 characters, each given once without regard to case. The dialog suggests the tags in use that contain the typed text, and Enter adds the typed tag.

Titles, addresses, paths and tags cannot contain control or format characters, such as line breaks or zero-width characters. Of a file edited by hand, links and tags that break these rules are left out, and only the first 20 of each are read.

---

## File Format

A metadata file is a JSON object and may have comments and a trailing comma. When HomeBlaze writes the file, it keeps the keys it does not know, writes the file indented and without comments, and deletes the file once it holds no key.

Keys and file names are matched exactly, case included. `NavTitle` is a key of its own that HomeBlaze does not read as `navTitle`, and a save that sets a known key in another case is refused with the right name. The file names end with `.metadata.json` in lower case.

### Subjects and Properties

`X.metadata.json` holds the metadata of the subject of `X.json`, also when `X.json` loads as a plain JSON file. Its keys at the top describe that subject, so a simple file looks like the file of a folder:

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

`Channel` in `$subjects` is the subject held by the property `Channel`, and in `$properties` the property itself. The form of browser addresses, such as `Channels/0/Power`, is read as well. Keys that start with `$` are reserved.

- An entry whose subject or property does not exist is kept and applies once it exists.
- Of two entries for the same subject or property, the first one applies, and saving metadata of that target is refused until one is removed.
- Items of a collection are matched by their index. When items change places, each keeps its metadata until the metadata file or the subject file changes, and then takes the metadata of its new index.

### Nested Storages

A storage that is stored as a JSON file inside another storage keeps all its metadata, its `order` included, in the `.metadata.json` of its own root. A sidecar beside its file in the other storage is not read.

### Limits

Saving metadata is refused beyond these limits. They are checked only on save, so a file edited by hand is read whatever its size.

- 64 keys per entry
- Key names of up to 64 characters
- Strings of up to 256 characters and values of up to 4 KB, except for `order` and for [`notes`](#notes), which have a limit of their own
- Files of up to 512 KB; a save that does not make a larger file larger is still allowed

### Reserved Names

- A file whose name ends with `.metadata.json` in lower case is never loaded as a subject. One that holds a `$type` is logged with a warning.
- A file whose name ends in another case, such as `Pump.Metadata.json`, is neither a subject nor a metadata file, and a warning asks to rename it in lower case.
- A subject cannot be named `Something.metadata`, in any case, as on a file system that ignores case its file would be the metadata file of `Something.json`.
- Deleting a subject also deletes its `X.metadata.json`. When you move or rename a file outside HomeBlaze, move or rename its metadata file with it.

### Files That Cannot Be Read

A metadata file with invalid JSON, or with a key that cannot be read, is ignored with a warning. The folder or subject then shows no metadata, and saving metadata or moving a child is refused until the file is fixed or deleted by hand. Every save reads the file again first, so a change made by hand that HomeBlaze has not loaded yet is kept.

### Bundled Documentation

When a [seed directory](configuration.md#homeblazeseeddirectory) is set, the documentation under `/Docs` is replaced on every start, so changes to its order or metadata are lost.

---

## Agents

Agents read metadata with the MCP tools. `browse` and `search` show the metadata of a subject as its property `HomeBlaze:Metadata` with `includeProperties`, and the metadata of a property as its attribute `@HomeBlaze:Metadata` with `includeAttributes`, both only where there is metadata. `get_property` reads paths such as `/Devices/Shelly/HomeBlaze:Metadata` and `/Devices/Shelly/Firmware@HomeBlaze:Metadata`.

`set_property` cannot change metadata. Agents find and change it with these methods through `invoke_query` and `invoke_operation`, when they may use these tools. The browser does not show the methods.

| Method | On | Description |
|--------|----|-------------|
| `SetMetadata` | A storage, such as the root `/` | Merges `values`, a JSON object, into the metadata of the subject at `path`, or of its property `property`. A key with a value is set, replacing a nested value as a whole, a key with `null` is removed, and every other key stays |
| `GetOrder` | A folder or a storage | Returns the names of the children in the order they are shown. The names include children that are hidden from agents |
| `SetOrder` | A folder or a storage | Puts the given `names` first, in that order, then the other children in the order they are shown. A name that is not a child, or that is given twice, is refused |
| `SearchMetadata` | A storage | Returns the subjects and properties whose metadata matches every filter that is given, see below |

`path` is the path of a subject relative to the storage, as `browse` shows it, such as `Devices/Shelly/Channels[0]`. It is empty for the storage itself, and a leading `/` is ignored. An example of `values`:

```json
{ "navTitle": "Hall light", "icon": null }
```

`SetMetadata` checks the [keys](#keys) that HomeBlaze reads. `title`, `navTitle` and `icon` take a string, and a blank string removes the key. `location` and `alignment` take one of their names. `notes`, `links` and `tags` follow the rules of [Notes, Links and Tags](#notes-links-and-tags), and a blank string or an empty array removes the key. `order`, of a folder or a storage only, takes names of children, which are checked as with `SetOrder`. Other keys are stored as given, within the [limits](#limits). A refused call returns the reason to the agent. Agents cannot change the metadata of an agent or of a subject inside one.

`SearchMetadata` takes these filters, each optional:

| Filter | Matches |
|--------|---------|
| `tag` | A tag in `tags`, without regard to case |
| `key` | A key that is set, such as `notes`, matched exactly |
| `value` | A string that equals the whole value, or a string in an array, of `key` when it is given, else of any key, without regard to case. Numbers and booleans never match |
| `path` | Subjects and properties at or below this path, relative to the storage |

Each match has the `path` of its subject and, for a property, its `property`, as `SetMetadata` takes them, sorted by path and then property. The MCP tools return at most the first 100 matches and leave out what they do not show. For agents they also leave out what `SetMetadata` would refuse them, such as the metadata of an agent.

To find text inside notes, agents use `search_markdown`, which searches the notes along with the pages and returns with each match in notes the path that `get_property` reads them with, see [Markdown Tools](../architecture/design/ai.md#markdown-tools).

Which tools an agent may use is set in [AI Agents](ai-agents.md#tools-and-permissions).

---

## Related Documentation

- [Markdown Pages](pages.md) - Frontmatter of pages, which keeps their title, icon and place in the navigation
- [Subjects, Storage & Files](subjects.md) - Subject files and the layout of the data folder
- [AI Agents](ai-agents.md) - Tools and permissions of agents
- [Metadata Design](../architecture/design/metadata.md) - How metadata is held, applied and written
