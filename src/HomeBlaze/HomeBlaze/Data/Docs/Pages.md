---
title: Markdown Pages
navTitle: Pages
position: 4
---

# Markdown Pages

HomeBlaze transforms markdown files into interactive pages with live data binding. This guide explains how to create dynamic pages using frontmatter, embedded expressions, and embedded subjects.

---

## How It Works

When you add a `.md` file to your data folder:
1. HomeBlaze detects it via file watching
2. The file becomes a `MarkdownFile` subject in the object graph
3. Frontmatter is parsed for navigation and display metadata
4. Expressions and subjects are extracted and tracked
5. The page renders with live data updates

---

## Frontmatter

Use YAML frontmatter at the top of your markdown file to control how it appears in navigation:

```yaml
---
title: My Dashboard
navTitle: Dashboard
icon: Dashboard
position: 1
---
```

### Frontmatter Options

| Property | Type | Description |
|----------|------|-------------|
| `title` | string | Full page title displayed in the header |
| `navTitle` | string | Short title for navigation menu (optional, defaults to `title`) |
| `icon` | string | MudBlazor icon name (e.g., `Dashboard`, `Settings`, `Article`) |
| `position` | number | Sort order in navigation (lower = higher in list) |
| `location` | string | Where to show in navigation: `NavBar` (sidebar, default) or `AppBar` (top bar) |
| `alignment` | string | AppBar alignment: `Left` (default) or `Right`. Only used when `location` is `AppBar` |

---

## Live Expressions

Embed live values from your subject graph using the `{{ path }}` syntax:

```markdown
Temperature: {{ mymotor.Temperature }}
Speed: {{ mymotor.CurrentSpeed }} RPM
```

### Expression Paths

Expressions resolve relative to the current page's embedded subjects first, then fall back to the global root:

| Path | Resolution |
|------|------------|
| `mymotor.Speed` | Inline subject named `mymotor` embedded in current page |
| `Root.Demo.Conveyor.CurrentSpeed` | Absolute path from root |

See [Configuration Guide - Path Syntax](Configuration.md#path-syntax) for full path documentation including `this.`, `../`, brackets for keys with dots, and more.

### Expression Features

- **Live updates**: Values update automatically when the source property changes
- **HTML-safe**: Values are automatically HTML-encoded
- **Null handling**: Missing or null values render as empty strings

---

## Embedded Subjects

Create subjects inline within your markdown using fenced code blocks:

~~~markdown
```subject(mymotor)
{
  "$type": "HomeBlaze.Samples.Motor",
  "name": "My Motor",
  "targetSpeed": 2000,
  "simulationInterval": "00:00:01"
}
```
~~~

### Syntax

- **Block type**: Use `subject(name)` as the language identifier
- **Name**: The `name` in parentheses becomes the subject's key for expression references
- **JSON content**: Valid JSON with `$type` discriminator for polymorphic deserialization

### Subject Lifecycle

- Subjects are created when the page loads
- If the subject type is a `BackgroundService`, it starts automatically
- Subjects are stopped and disposed when the page is removed
- Configuration changes in the JSON are applied reactively

### Widget Rendering

If a subject has a registered widget component, it renders inline:

~~~markdown
Here's my motor:

```subject(motor1)
{
  "$type": "HomeBlaze.Samples.Motor",
  "name": "Primary Motor",
  "targetSpeed": 1500
}
```

The motor's current speed is {{ motor1.CurrentSpeed }} RPM.
~~~

---

## Relative Links

Links between markdown pages use relative paths that HomeBlaze converts to proper routes:

```markdown
See the [Architecture Guide](Architecture.md) for details.

Check the [demo setup](../Demo/Setup.md) for examples.
```

### Link Resolution

| Markdown Link | Route |
|--------------|-------|
| `[Link](sibling.md)` | Same folder |
| `[Link](subfolder/page.md)` | Nested folder |
| `[Link](../parent.md)` | Parent folder |
| `[Link](../../root.md)` | Two levels up |

---

## Example: Creating a Dashboard

Here's a complete example of a dashboard page:

```markdown
---
title: Factory Dashboard
navTitle: Dashboard
icon: Dashboard
position: 0
---

# Factory Status

## Motors

### Conveyor Belt

```subject(conveyor)
{
  "$type": "HomeBlaze.Samples.Motor",
  "name": "Conveyor Belt",
  "targetSpeed": 600,
  "simulationInterval": "00:00:02"
}
```

Current speed: **{{ conveyor.CurrentSpeed }}** RPM

### Cooling Fan

```subject(cooler)
{
  "$type": "HomeBlaze.Samples.Motor",
  "name": "Cooling Fan",
  "targetSpeed": 1800,
  "simulationInterval": "00:00:01"
}
```

Temperature: **{{ cooler.Temperature }}** C

---

For more details, see [Motor Setup Guide](../samples/MotorSetup.md).
```

---

## Tips

1. **Use descriptive subject names**: Names like `primaryMotor` are clearer than `m1`
2. **Order your frontmatter**: Use `order` to control navigation position
3. **Combine widgets and expressions**: Show a widget, then reference its properties in text
4. **Keep pages focused**: One topic per page makes navigation cleaner

---

## Related Documentation

- [Building Custom Subjects](BuildingSubjects.md) - Create subject types for embedding
- [Configuration Guide](Configuration.md) - Configure storage and subjects
- [Demo Setup Guide](../Demo/Setup.md) - See embedded subjects in action
