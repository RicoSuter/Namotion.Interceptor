---
title: Metadata
navTitle: Metadata
status: Partial
---

# Metadata Design

## Overview

Metadata is keys and JSON values that describe a folder, a subject or a property, such as its title and icon in the navigation, the order of a folder, notes, links and tags, kept in `.metadata.json` files of a storage, separate from the configuration of subjects, so that operators and integrators enrich the knowledge graph without modifying subject code. People change it in the browser and agents through methods of the storage, and both go through one write path. [Metadata](../../administration/metadata.md) is the user guide with the keys, the file format and the limits. This page is the design.

## Runtime Model [Implemented]

```
Devices/Shelly                       HomeBlaze:Metadata             = { navTitle, icon }
Devices/Shelly/Channels[0]           HomeBlaze:Metadata             = { navTitle }
Devices/Shelly/Channels[0]/Power     Power@HomeBlaze:Metadata       = { comment }
Devices/Pump                         HomeBlaze:Metadata             = { notes, links, tags }
Docs (folder)                        HomeBlaze:Metadata             = { title, order }
```

- **Value.** The registry holds metadata as an immutable dictionary of key to JSON value, `ImmutableDictionary<string, JsonElement>`: that of a subject in its dynamic property `HomeBlaze:Metadata`, that of a property in its attribute of that name.
- **No setter.** Both change only through the store, so `set_property` cannot change metadata.
- **Added on demand.** The property or attribute is added only once an entry exists. The registry cannot remove a property, so metadata that is removed sets the value to null, and null means no metadata.
- **One step with its file.** Setting a value and reading the file it comes from are one step under the `SyncRoot` of the subject, so a subject that attaches during a write takes the new value.
- **Readers.** Navigation, the browser and the order of children read the known keys (`MetadataKeys`) from these dictionaries through `SubjectNavigationExtensions` and `ChildOrdering`. The path picker does not list the metadata.
- **Contracts.** `IMetadataStore`, `IMetadataContainer` and `MetadataKeys` are in `HomeBlaze.Storage.Abstractions`, next to `IConfigurationWriter`.

## Files and Applying [Implemented]

- **Index.** Metadata files (`.metadata.json` in a folder, `X.metadata.json` beside `X.json`) are indexed like every other file, by version and hash, but never become subjects. `.metadata.json` is the only dot file the storage does not ignore.
- **Apply.** After every assignment of the tree the storage applies the files: the file of the root and of each folder to it, with its order, and the file beside each JSON file to the subject of that file and to every subject and property inside it, by their paths relative to that subject. A sidecar is applied again only when the file or its subject changed.
- **Later arrivals.** A subject that attaches later, and a property other than a method added to a subject later, take their entries from the file applied last, which `MetadataLifecycleHandler` asks the store of the subject for, the nearest `IMetadataStore` up the first parents. The applied files hold paths and JSON only, and the storage keeps them per subject without keeping the subject.
- **Owner of a sidecar.** A sidecar belongs to the path of the JSON file, whatever its subject is, and is found by its exact path.
- **Moves.** Items of a collection are matched by their index, and a subject that moves can keep the metadata of its previous place, see [Collections and Moves](../../administration/metadata.md#collections-and-moves).

## Writing [Implemented]

- **One write path.** Every write of metadata or order runs on the worker of the storage through `IMetadataStore.SetMetadataAsync`, which merges a JSON object into the entry of its target at the top level. The order of a folder is its key `order`, written through the extension method `SetOrderAsync`.
- **Fresh file.** Each write reads and parses the file fresh first, so a change made by hand that the storage has not loaded yet is kept. It records version and hash like any other write, so it does not reload anything, and the values in memory are current when it returns.
- **Cleanup.** A metadata file that would be empty is deleted, and deleting a subject deletes its metadata file, also after its caller canceled.
- **Refusals.** A write that cannot be done, such as one to a file that cannot be read or to a storage that is not connected, is refused with a message that says why.

## Agent Access [Implemented]

Agents read metadata with `browse`, `search` and `get_property`, which list it only where it has a value, find it with the method `SearchMetadata` and change it with the methods `SetMetadata`, `GetOrder` and `SetOrder` through `invoke_operation` and `invoke_query`, without tools of their own. Agents cannot change the metadata of an agent or of a subject inside one. [MCP Tool Layering](ai.md#mcp-tool-layering-implemented) and [Write Limits](ai.md#write-limits-implemented) describe how the tools do this, and [Agents](../../administration/metadata.md#agents) describes the methods and their arguments.

## Notes, Links and Tags [Implemented]

`notes`, `links` and `tags` are known keys of `MetadataKeys`, whose rules and display are in [Notes, Links and Tags](../../administration/metadata.md#notes-links-and-tags). `StorageMetadataWriter` refuses a write that breaks the rules, from the browser and from agents alike, and the read helpers of `MetadataDictionaryExtensions` leave out what breaks them in a file edited by hand. `MetadataNotes` checks and renders notes, `MetadataLinkResolver` resolves a `MetadataLink` for the browser, and `MetadataSummary` shows them in the pane, in the dialog of the **Metadata** button and in the tooltip of the row of a property.

## Search [Implemented]

- **`MetadataSearch`.** One service finds metadata for the storage, the tag filter and the suggested tags. It walks the tree below a subject on each call and keeps nothing, matches a key that is set exactly, and a tag and a string value without regard to case, and sorts the matches by path and then property. A subject that the caller excludes is skipped with everything below it.
- **`SearchMetadata`.** A `[Query]` of a storage over `MetadataSearch`, see [Agents](../../administration/metadata.md#agents) for its arguments and result and [Write Limits](ai.md#write-limits-implemented) for what the MCP tools leave out.
- **Tag filter.** A click on a tag opens `MetadataTagFilterDialog`, which lists everything with the tag, without subjects excluded from user interfaces and what is below them, and without properties that the browser does not show. The tag field of the **Metadata** dialog loads the tags in use once when the dialog opens, with the same exclusions, and suggests those that contain the typed text.
- **Notes in `search_markdown`.** `MarkdownSearchSourceFinder` gives `search_markdown` the pages and the notes of metadata in one list in path order, and a match in notes has `kind: notes`, the path of its subject and the name of its property, see [Markdown Tools](ai.md#markdown-tools).

## Limits

The limits of entries, keys, values and files are in [Limits](../../administration/metadata.md#limits), and those of notes, links and tags in [Notes, Links and Tags](../../administration/metadata.md#notes-links-and-tags). A write reads and parses the whole file, which is fine at that size because metadata changes rarely.

## Decisions

- **A dictionary instead of typed records.** Metadata is a generic dictionary of key to JSON value, so a new key needs no new type, and keys that HomeBlaze does not know are kept, whether a person, an agent or a later version wrote them.
- **A dynamic property and an attribute.** The metadata of a subject is a dynamic property and that of a property an attribute, both named `HomeBlaze:Metadata`, so the registry and the MCP tools carry it like any other value, and no new tools are needed.
- **A sidecar per stored file.** A subject inside a stored subject has no file of its own, so the sidecar of the JSON file covers that subject and its whole tree, and it is versioned, moved and deleted with that file. The file of a folder holds only the keys of the folder and its order, because its children have files of their own.
- **Exact keys.** Keys are matched exactly, case included, like the keys of subject files and frontmatter, so each key has one spelling and a file with keys that differ only in case can still be read. A known key in another case is refused on save instead of being stored where HomeBlaze does not read it.
- **Paths instead of a nested tree.** Most subjects and properties have no metadata, and flat path keys under `$subjects` and `$properties` are easier to read and to write for people and agents than a tree that mirrors the subject. The two maps tell "the metadata of the property `Channel`" apart from "the metadata of the subject held by `Channel`".
- **One write path.** The browser and agents both write through `IMetadataStore`, so they get the same merge, validation and refusals, and the file and the values in memory agree.
- **The whole file applied again.** After a load or a write the whole file is applied to its tree instead of updating single entries. This is simple, applies a hand edit and a write the same way, and costs little because metadata changes rarely.
- **Nested storages keep their own metadata.** A storage stored inside another storage keeps all its metadata in its own root, so each file has one owner and a storage keeps its metadata wherever it is mounted.
- **Agents cannot change the metadata of agents.** Like the settings and operations of agents, this keeps an agent from changing agents.
- **Null attributes left out by MCP.** Because the registry cannot remove an attribute, removed metadata stays as a null value, and the MCP tools leave out read-only attributes without a value, as the metadata attribute is, so that it reads as no metadata.
- **Notes under the rules of pages.** Notes are shown inside the page of the application, so they follow the rules for new content of pages and cannot run or load anything.
- **Tags as plain strings.** A tag needs no definition before it is used, and tags are unique without regard to case so that `Floor-2` and `floor-2` are one tag.
- **No search cache.** Metadata changes rarely and walking the tree is cheap enough, while a cache would need to be invalidated on every change of a path.
- **Notes found by `search_markdown`.** Notes are searched together with the pages in one search instead of by a tool of their own, so an agent finds text with one call wherever it is written.
- **User content, not instructions.** Metadata is written by users, so the server instructions and the preamble of the built-in agents tell agents to treat every value of it as data and never as instructions.

## Planned

- **Metadata in search results.** `SearchMetadata` returning the metadata of each match (`includeMetadata`), so that an agent needs no further call to read it.
- **Several targets in one call.** One `SetMetadata` call for several subjects and properties, grouped into one write per file.
- **Annotations.** More known keys that group subjects across folders, such as `area: "Kitchen"` (the equivalent of Home Assistant areas), `group: "cooling-system"` or `owner: "maintenance-team-b"`. Until then such keys are stored as given. All annotations are meant to be queryable through the registry, transmitted over the wire via WebSocket sync and visible in the UI, without a separate subsystem.
- **Page blocks.** Metadata of the subjects that blocks of a Markdown page create, kept in the frontmatter of the page.
- **Display overrides.** A display name or a unit of a property taken from its metadata.
- **Generic search.** Attribute filters in the generic `search` tool of the library.
- **Naming.** `IConfigurationWriter` renamed to `IConfigurationStore`, next to `IMetadataStore`.
- **Custom root files.** A `root.metadata.json` for subjects in a custom `root.json`. The default `root.json` holds only the root storage.
- **Search cache.** A cache for the metadata search and the suggested tags, if walking the tree on demand turns out too slow.
- **Content search.** The MCP tool `search_markdown` renamed to `search_content`, since it searches pages, documents and notes.
- **Known keys checked once.** The known keys of a file checked when it is read, instead of on every read of a value.
- **Entries without a target.** A list of the entries that match no subject or property.
- **Library support.** Removing a dynamic property or attribute, and telling whether a property was attached with its subject, which would simplify `MetadataLifecycleHandler` and drop the null values of removed metadata.
- **Metadata of files.** Notes and tags for pages and other files, in the frontmatter of a page and in a sidecar such as `Manual.pdf.metadata.json`.
- **Properties without state.** The metadata of a property that is not `[State]` can be written but is not shown by `browse`, because the property itself is not listed. Whether such a property is listed when it has metadata is open.
