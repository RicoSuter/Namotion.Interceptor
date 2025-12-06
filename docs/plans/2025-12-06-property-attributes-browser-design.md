# Property Attributes in Browser Design

## Overview

Extend the SubjectPropertyPanel to display property attributes (metadata like min/max values) as expandable sub-rows under properties.

## Motor Sample Changes

Add min/max attributes to `TargetSpeed` using `[PropertyAttribute]`:

```csharp
// In Motor.cs

[Configuration]
[State("Target", Order = 2)]
public partial int TargetSpeed { get; set; }

[PropertyAttribute(nameof(TargetSpeed), "Minimum")]
public partial int TargetSpeed_Minimum { get; set; } = 0;

[PropertyAttribute(nameof(TargetSpeed), "Maximum")]
public partial int TargetSpeed_Maximum { get; set; } = 3000;
```

Only `TargetSpeed` gets attributes since it's user-configurable input. `CurrentSpeed` and `Temperature` are outputs.

## UI Design

Visual representation:

```
▶ TargetSpeed: 1500          ← collapsed (click to expand)

▼ TargetSpeed: 1500          ← expanded
    Minimum: 0
    Maximum: 3000
```

- `▶` = collapsed - "click to open"
- `▼` = expanded - arrow points to content below
- Properties without attributes show no arrow
- Arrow appears left of property name
- Attributes render indented below when expanded

## Implementation

### SubjectPropertyPanel.razor

Update property rendering loop:

```razor
@foreach (var prop in GetPrimitiveProperties())
{
    var value = prop.GetValue();
    var attributes = prop.Attributes.ToArray();
    var hasAttributes = attributes.Length > 0;
    var isExpanded = _expandedProperties.Contains(prop.Name);

    <div class="mb-1">
        @if (hasAttributes)
        {
            <MudIconButton
                Icon="@(isExpanded ? Icons.Material.Filled.ArrowDropDown : Icons.Material.Filled.ArrowRight)"
                Size="Size.Small"
                OnClick="@(() => TogglePropertyExpanded(prop.Name))" />
        }
        <strong>@GetPropertyDisplayName(prop): </strong>
        @RenderPropertyValue(prop, value)
    </div>

    @if (isExpanded)
    {
        <div style="margin-left: 32px;">
            @foreach (var attr in attributes)
            {
                <MudText Class="mb-1" Style="opacity: 0.8">
                    <strong>@attr.Name: </strong>@attr.GetValue()
                </MudText>
            }
        </div>
    }
}
```

Add state tracking:

```csharp
private HashSet<string> _expandedProperties = new();

private void TogglePropertyExpanded(string propertyName)
{
    if (!_expandedProperties.Remove(propertyName))
        _expandedProperties.Add(propertyName);
}
```

## Files to Modify

1. `HomeBlaze.Core/Subjects/Motor.cs` - Add `[PropertyAttribute]` properties for TargetSpeed min/max
2. `HomeBlaze/Components/SubjectPropertyPanel.razor` - Add attribute expansion UI

## Key Design Decisions

1. **Single enumeration** - Cache `prop.Attributes.ToArray()` to avoid multiple enumeration
2. **Arrow left of name** - Consistent with tree view patterns
3. **No "Attributes" label** - Arrow presence implies attributes exist
4. **HashSet for state** - Simple toggle tracking per property name
