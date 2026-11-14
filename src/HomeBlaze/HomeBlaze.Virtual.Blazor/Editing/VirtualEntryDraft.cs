using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Metadata;

namespace HomeBlaze.Virtual.Blazor.Editing;

/// <summary>An editable copy of one entry of the properties of a virtual subject.</summary>
internal sealed class VirtualEntryDraft
{
    public VirtualEntryDraft(string name, VirtualEntryKind kind, bool isAttribute, VirtualEntryDraft? parent)
    {
        Name = name;
        Kind = kind;
        IsAttribute = isAttribute;
        Parent = parent;
    }

    public string Name { get; set; }

    public VirtualEntryKind Kind { get; set; }

    public bool IsAttribute { get; }

    public VirtualEntryDraft? Parent { get; }

    public string? Expression { get; set; }

    public string? Path { get; set; }

    public VirtualDataType? DataType { get; set; }

    public bool IsState { get; set; }

    public string? Title { get; set; }

    public StateUnit? Unit { get; set; }

    public int? Position { get; set; }

    public string? Category { get; set; }

    public bool IsCumulative { get; set; }

    public bool IsDiscrete { get; set; }

    public bool IsEstimated { get; set; }

    public List<VirtualEntryDraft> Properties { get; } = [];

    public List<VirtualEntryDraft> Attributes { get; } = [];

    /// <summary>The path in the tree, such as <c>Pv/Power@Maximum</c>, as the validator reports issues.</summary>
    public string GetPath()
    {
        if (Parent is null)
        {
            return Name;
        }

        var separator = IsAttribute ? '@' : '/';
        return Parent.GetPath() + separator + Name;
    }

    public static VirtualEntryDraft From(string name, VirtualProperty property, bool isAttribute, VirtualEntryDraft? parent)
    {
        var entry = new VirtualEntryDraft(name, GetKind(property), isAttribute, parent)
        {
            Expression = property.Expression,
            Path = property.Path,
            DataType = property.DataType
        };

        if (property.State is { } state)
        {
            entry.SetState(state);
        }

        if (property.Properties is { } children)
        {
            foreach (var (childName, child) in children)
            {
                entry.Properties.Add(From(childName, child, isAttribute: false, entry));
            }
        }

        if (property.Attributes is { } attributes)
        {
            foreach (var (attributeName, attribute) in attributes)
            {
                entry.Attributes.Add(From(attributeName, attribute, isAttribute: true, entry));
            }
        }

        return entry;
    }

    public VirtualProperty ToProperty()
    {
        if (Kind == VirtualEntryKind.Subject)
        {
            return new VirtualProperty { Properties = ToDictionary(Properties) ?? new Dictionary<string, VirtualProperty>() };
        }

        return new VirtualProperty
        {
            Expression = Kind == VirtualEntryKind.Expression ? Expression ?? string.Empty : null,
            Path = Kind == VirtualEntryKind.Binding ? Path ?? string.Empty : null,
            DataType = DataType,
            State = IsState ? ToState() : null,
            Attributes = ToDictionary(Attributes)
        };
    }

    /// <summary>
    /// Takes over the data type of a binding target, and its state with its unit and, unless the entry has one, its
    /// title; a target without state clears the unit.
    /// </summary>
    public void TakeOver(Type targetType, StateMetadata? state)
    {
        if (VirtualDataTypes.TryGetDataType(targetType, out var dataType))
        {
            DataType = dataType;
        }

        if (state is null)
        {
            Unit = null;
            return;
        }

        IsState = true;
        Unit = state.Unit == StateUnit.Default ? null : state.Unit;
        if (string.IsNullOrWhiteSpace(Title))
        {
            Title = state.Title;
        }
    }

    // The first of entries with the same name wins; the draft reports the others.
    public static Dictionary<string, VirtualProperty>? ToDictionary(List<VirtualEntryDraft> entries)
    {
        if (entries.Count == 0)
        {
            return null;
        }

        var dictionary = new Dictionary<string, VirtualProperty>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            dictionary.TryAdd(entry.Name, entry.ToProperty());
        }

        return dictionary;
    }

    private static VirtualEntryKind GetKind(VirtualProperty property)
    {
        if (property.Properties is not null)
        {
            return VirtualEntryKind.Subject;
        }

        return property.Path is not null ? VirtualEntryKind.Binding : VirtualEntryKind.Expression;
    }

    private void SetState(VirtualState state)
    {
        IsState = true;
        Title = state.Title;
        Unit = state.Unit;
        Position = state.Position;
        Category = state.Category;
        IsCumulative = state.IsCumulative == true;
        IsDiscrete = state.IsDiscrete == true;
        IsEstimated = state.IsEstimated == true;
    }

    private VirtualState ToState() => new()
    {
        Title = string.IsNullOrWhiteSpace(Title) ? null : Title.Trim(),
        Unit = Unit is null or StateUnit.Default ? null : Unit,
        Position = Position,
        Category = string.IsNullOrWhiteSpace(Category) ? null : Category.Trim(),
        IsCumulative = IsCumulative ? true : null,
        IsDiscrete = IsDiscrete ? true : null,
        IsEstimated = IsEstimated ? true : null
    };
}
