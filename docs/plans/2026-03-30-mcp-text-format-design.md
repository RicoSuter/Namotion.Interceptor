# MCP Text Output Format

## Problem

MCP tool responses (browse, search) return compact JSON, which is difficult for LLMs to reason about. Deeply nested JSON requires tracking brace nesting, matching keys across levels, and mentally separating metadata (`$path`, `$type`) from data (`Speed`, `Children`). The deeper the subject tree, the harder it gets.

Generic formats like YAML or pretty-printed JSON improve readability but remain verbose because every property must spell out `value:`, `type:`, `isWritable:` as separate keys. Since the data model has a consistent shape (subjects with path/type/title, properties with value/type/writable), a purpose-built format can compress this into a convention that is both more compact and easier for LLMs to scan.

## Decision

Add a `format` parameter to `browse` and `search` tools with two options:

- **`text`** (default) -- purpose-built indented format optimized for LLM comprehension; one subject per line, one property per line, tree structure via indentation
- **`json`** -- pretty-printed (indented) JSON output, for when the LLM needs exact structured data

## Scope

- `browse` tool: text format support
- `search` tool: text format support
- Other tools (`list_types`, `get_property`, `set_property`, `invoke_method`): unchanged, JSON only

## Text Format Specification

### Legend Header

Every text response starts with a compact format legend (MCP calls are stateless, so the LLM may not have prior context):

```
# path [Type] "title" $key=value | prop: value | type | @attr: value | Collection/ (Nx Type)
# Use get_property for exact values or browse with format=json for structured data.
```

### Subject Line

```
path [FullyQualifiedType] "title" $enrichmentKey=value
```

- **path**: full `$path` as used by `get_property`/`set_property` (e.g., `/Demo/WaterPump`)
- **type**: fully qualified .NET type name in brackets (usable with search `types` parameter); the `$type` enrichment is folded into the `[brackets]` and not repeated as an enrichment
- **title**: in double quotes, omitted if not set
- **enrichments**: `$key=value` pairs from subject enrichers (e.g., `$icon=Settings`), excluding `$type` (shown in brackets)

### Properties (when `includeProperties=true`)

Indented under the subject, with `|` separating value from type metadata:

```
  PropertyName: value | type
  PropertyName: value | type, writable
```

- Value rendering:
  - `null` for null values
  - `""` for empty strings
  - Strings longer than 100 characters are truncated with `...`
  - All other values rendered inline without quotes
- Type is the JSON schema type (`string`, `integer`, `number`, `boolean`)
- `, writable` suffix when the property has a setter and server is not read-only

### Attributes (when `includeAttributes=true`)

Indented under the property, prefixed with `@`:

```
    @AttributeName: value
    @AttributeName: {"complex": "json", "object": true}
```

- Scalar values (`int`, `string`, `bool`): rendered as plain values
- Complex objects: rendered as inline JSON

### Methods (when `includeMethods=true`)

```
  methods: Method1() Method2() Method3()
```

Method names on a single line. Use `list_types` for full signatures with parameters.

### Interfaces (when `includeInterfaces=true`)

```
  interfaces: IMotor, IDevice, ITemperatureSensor
```

Comma-separated on a single line.

### Collapsed Collections (browse, at depth boundary)

When children exist but depth limit is reached:

```
  CollectionName/ (Nx ItemType)
  CollectionName/ (N children)
```

- `Nx ItemType` when all children share the same type
- `N children` when types are mixed

### Footer

```
[1 subject]
[9 subjects]
[3 subjects, truncated]
```

- Always present as the last line
- Singular/plural: `subject` for 1, `subjects` for 0 or 2+
- `truncated` indicates more results exist; the LLM should narrow the query or increase `maxSubjects`

### Browse vs Search Differences

**Browse** uses tree indentation to show hierarchy:

```
/ [RootType] "Root"
  /Child [ChildType] "Child Title"
    /Child/Grandchild [GrandchildType] "Grandchild"
```

**Search** uses blank lines between subjects (flat list):

```
/Demo/Motor1 [Motor] "Motor 1"
  Speed: 1500 | integer

/Demo/Motor2 [Motor] "Motor 2"
  Speed: 2400 | integer

[2 subjects]
```

## Full Examples

### Browse -- default (depth=1, no properties)

```
# path [Type] "title" $key=value | prop: value | type | @attr: value | Collection/ (Nx Type)
# Use get_property for exact values or browse with format=json for structured data.

/ [HomeBlaze.Storage.FluentStorageContainer] "Data" $icon=Storage
  /Settings.md [HomeBlaze.Storage.Files.MarkdownFile] "Settings" $icon=Settings
    Children/ (1x HtmlSegment)
  /Dashboard.md [HomeBlaze.Storage.Files.MarkdownFile] "Dashboard" $icon=Dashboard
    Children/ (31 children)
  /Gpio [Namotion.Devices.Gpio.GpioSubject] "GPIO" $icon=Memory
    Pins/ (28x GpioPin)
  /Demo [HomeBlaze.Storage.VirtualFolder] "Demo" $icon=Folder
    Children/ (7 children)
  /Servers [HomeBlaze.Storage.VirtualFolder] "Servers" $icon=Folder
    Children/ (1x OpcUaServer)
[9 subjects]
```

### Browse -- depth=0, includeProperties + includeMethods + includeAttributes

```
# path [Type] "title" $key=value | prop: value | type | @attr: value | Collection/ (Nx Type)
# Use get_property for exact values or browse with format=json for structured data.

/Demo/WaterPump [HomeBlaze.Samples.Motor] "Water Pump" $icon=Settings
  TargetSpeed: 2400 | integer, writable
    @Minimum: 0
    @Maximum: 3000
    @Configuration: {}
    @State: {"Title":"Target","Unit":0,"Position":2,"IsCumulative":false,"IsDiscrete":false,"IsEstimated":false}
  CurrentSpeed: 2400 | integer, writable
    @State: {"Title":"Speed","Unit":0,"Position":3,"IsCumulative":false,"IsDiscrete":false,"IsEstimated":false}
  Temperature: 73.35 | number, writable
    @State: {"Title":null,"Unit":2,"Position":4,"IsCumulative":false,"IsDiscrete":false,"IsEstimated":false}
  Status: Running | string, writable
    @State: {"Title":null,"Unit":0,"Position":1,"IsCumulative":false,"IsDiscrete":false,"IsEstimated":false}
  SpeedDelta: 0 | integer
    @State: {"Title":"Delta","Unit":0,"Position":5,"IsCumulative":false,"IsDiscrete":false,"IsEstimated":false}
  IsAtTargetSpeed: true | boolean
    @State: {"Title":"At Target","Unit":0,"Position":6,"IsCumulative":false,"IsDiscrete":false,"IsEstimated":false}
  methods: SetTargetSpeed() EmergencyStop() GetDiagnostics() RunTest()
[1 subject]
```

### Search -- types filter, includeProperties

```
# path [Type] "title" $key=value | prop: value | type | @attr: value | Collection/ (Nx Type)
# Use get_property for exact values or browse with format=json for structured data.

/Demo/ExhaustFan [HomeBlaze.Samples.Motor] "Exhaust Fan" $icon=Settings
  TargetSpeed: 1500 | integer, writable
  CurrentSpeed: 1500 | integer, writable
  Temperature: 54.19 | number, writable
  Status: Running | string, writable
  SpeedDelta: 0 | integer
  IsAtTargetSpeed: true | boolean

/Demo/Compressor [HomeBlaze.Samples.Motor] "Compressor" $icon=Settings
  TargetSpeed: 3000 | integer, writable
  CurrentSpeed: 2300 | integer, writable
  Temperature: 70.04 | number, writable
  Status: Running | string, writable
  SpeedDelta: 700 | integer
  IsAtTargetSpeed: false | boolean

[3 subjects, truncated]
```

### Search -- minimal (no properties)

```
# path [Type] "title" $key=value | prop: value | type | @attr: value | Collection/ (Nx Type)
# Use get_property for exact values or browse with format=json for structured data.

/Demo/ExhaustFan [HomeBlaze.Samples.Motor] "Exhaust Fan"
/Demo/Compressor [HomeBlaze.Samples.Motor] "Compressor"
/Demo/Inline.md/mymotor [HomeBlaze.Samples.Motor] "MyMotor"

[3 subjects, truncated]
```

## Tool Descriptions

The tool descriptions should guide the LLM on when to use which format:

**browse:**
> Browse the subject tree at a path with configurable depth.
> Default text format is optimized for overview and navigation.
> Use format=json when you need exact property values or structured data for processing.
> Use depth=0 with includeProperties=true to see all properties of a subject.
> Paths: '/' separators, brackets for indices (Pins[0]).
> To find subjects by type, use search with types instead.

**search:**
> Search subjects by text and/or type/interface names.
> Default text format is optimized for scanning results.
> Use format=json when you need exact property values or structured data for processing.
> Use list_types to discover interface names first, then pass to types parameter.
> Returns flat list of matching subjects with paths.

## Implementation Notes

### Format Parameter

- `format` parameter added to both `browse` and `search` input schemas: `enum ["text", "json"]`, default `"text"`
- When `format=json`, output is serialized via `System.Text.Json` with `WriteIndented = true` for pretty-printed JSON
- When `format=text`, the handler returns a pre-formatted string via `McpTextFormatter`

### DTO Model

The implementation uses typed DTOs (`SubjectNode`, `SubjectNodeProperty` hierarchy) instead of `Dictionary<string, object?>`:

- **`SubjectNode`** — tree node with `$path`, `$type`, `$title`, enrichments (via `[JsonExtensionData]`), `methods`, `interfaces`, `properties`
- **`SubjectNodeProperty`** — polymorphic base with `[JsonPolymorphic]` discriminator `kind`:
  - `ScalarProperty` (`kind: "value"`) — scalar value with type, writable flag, optional attributes
  - `SubjectObjectProperty` (`kind: "object"`) — single subject reference with `IsCollapsed` flag
  - `SubjectCollectionProperty` (`kind: "collection"`) — ordered list of subjects with `IsCollapsed`, `Count`, `ItemType`
  - `SubjectDictionaryProperty` (`kind: "dictionary"`) — keyed map of subjects with `IsCollapsed`, `Count`, `ItemType`
- **`BrowseResult`** / **`SearchResult`** — wrapper DTOs with `result`/`results`, `subjectCount`, `truncated`

### Text Formatter (`McpTextFormatter`)

A shared static class used by both browse and search, with two entry points:

- `FormatBrowseResult(...)` — formats the tree with indentation for parent-child hierarchy
- `FormatSearchResult(...)` — formats a flat list with blank lines between subjects

Both call shared helpers for subject lines, property lines, attribute lines, etc.

### Key Rules

- The legend is a compile-time constant string, not dynamically generated
- The `$type` enrichment is extracted from enrichments and placed in the `[Type]` bracket on the subject line
- Attribute values: use `JsonSerializer.Serialize()` for non-primitive types to produce inline JSON
- Property value truncation: strings longer than 100 characters are truncated with `...`
- Null values rendered as literal `null`; empty strings rendered as `""`
- Singular/plural footer: `[1 subject]` vs `[N subjects]`

### Serialization Integration

The handler return type changes based on format:
- `format=json`: return the `BrowseResult`/`SearchResult` DTO (serialized with `WriteIndented = true`)
- `format=text`: return a pre-formatted string (the `TextContentBlock` in `ServiceCollectionExtensions` detects string results and skips JSON serialization)
