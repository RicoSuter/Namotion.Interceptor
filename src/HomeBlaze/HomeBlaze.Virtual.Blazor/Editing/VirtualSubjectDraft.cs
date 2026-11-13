using System.Collections.Immutable;

namespace HomeBlaze.Virtual.Blazor.Editing;

/// <summary>An editable copy of the configuration of a virtual subject, applied to it on save.</summary>
internal sealed class VirtualSubjectDraft
{
    private VirtualSubjectDraft(string? title)
    {
        Title = title;
    }

    public string? Title { get; set; }

    public List<VirtualEntryDraft> Properties { get; } = [];

    public static VirtualSubjectDraft From(VirtualSubject subject) => From(subject.Title, subject.Properties);

    public static VirtualSubjectDraft From(string? title, IReadOnlyDictionary<string, VirtualProperty>? properties)
    {
        var draft = new VirtualSubjectDraft(title);
        if (properties is not null)
        {
            foreach (var (name, property) in properties)
            {
                draft.Properties.Add(VirtualEntryDraft.From(name, property, isAttribute: false, parent: null));
            }
        }

        return draft;
    }

    public void ApplyTo(VirtualSubject subject)
    {
        subject.Title = string.IsNullOrWhiteSpace(Title) ? null : Title.Trim();
        subject.Properties = ToProperties();
    }

    public Dictionary<string, VirtualProperty>? ToProperties() => VirtualEntryDraft.ToDictionary(Properties);

    /// <summary>Gets the issues of the draft; <paramref name="ownPath"/> is the path of the subject, to find cycles through it.</summary>
    public ImmutableArray<VirtualPropertyIssue> Validate(string? ownPath = null)
    {
        var issues = new List<VirtualPropertyIssue>();
        FindDuplicateNames(Properties, issues);
        issues.AddRange(VirtualPropertyValidator.Validate(ToProperties(), ownPath));
        return [.. issues];
    }

    /// <summary>Adds an expression property to the subject that holds <paramref name="selected"/>, or to the root.</summary>
    public VirtualEntryDraft AddProperty(VirtualEntryDraft? selected)
    {
        var owner = FindSubject(selected);
        var siblings = owner?.Properties ?? Properties;
        var entry = new VirtualEntryDraft(UniqueName(siblings, "Property"), VirtualEntryKind.Expression, isAttribute: false, owner)
        {
            DataType = VirtualDataType.Number,
            IsState = true
        };
        siblings.Add(entry);
        return entry;
    }

    /// <summary>Adds a nested subject to the subject that holds <paramref name="selected"/>, or to the root.</summary>
    public VirtualEntryDraft AddSubject(VirtualEntryDraft? selected)
    {
        var owner = FindSubject(selected);
        var siblings = owner?.Properties ?? Properties;
        var entry = new VirtualEntryDraft(UniqueName(siblings, "Group"), VirtualEntryKind.Subject, isAttribute: false, owner);
        siblings.Add(entry);
        return entry;
    }

    /// <summary>Adds a constant attribute to an expression or binding property.</summary>
    public VirtualEntryDraft AddAttribute(VirtualEntryDraft property)
    {
        var entry = new VirtualEntryDraft(UniqueName(property.Attributes, "Minimum"), VirtualEntryKind.Expression, isAttribute: true, property)
        {
            Expression = "0",
            DataType = VirtualDataType.Number
        };
        property.Attributes.Add(entry);
        return entry;
    }

    public void Remove(VirtualEntryDraft entry)
    {
        if (entry.Parent is not { } parent)
        {
            Properties.Remove(entry);
        }
        else if (entry.IsAttribute)
        {
            parent.Attributes.Remove(entry);
        }
        else
        {
            parent.Properties.Remove(entry);
        }
    }

    /// <summary>Finds the entry with the path, as the validator reports it.</summary>
    public VirtualEntryDraft? Find(string path) => Find(Properties, path);

    private static VirtualEntryDraft? Find(List<VirtualEntryDraft> entries, string path)
    {
        foreach (var entry in entries)
        {
            if (entry.GetPath() == path)
            {
                return entry;
            }

            var found = Find(entry.Properties, path) ?? Find(entry.Attributes, path);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>Finds the subject entry that entries added with <paramref name="selected"/> go into; null for the root.</summary>
    public static VirtualEntryDraft? FindSubject(VirtualEntryDraft? selected)
    {
        var current = selected;
        while (current is not null && current.Kind != VirtualEntryKind.Subject)
        {
            current = current.Parent;
        }

        return current;
    }

    private static string UniqueName(List<VirtualEntryDraft> siblings, string baseName)
    {
        var name = baseName;
        var number = 2;
        while (siblings.Exists(entry => entry.Name == name))
        {
            name = baseName + number;
            number++;
        }

        return name;
    }

    private static void FindDuplicateNames(List<VirtualEntryDraft> entries, List<VirtualPropertyIssue> issues)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (!names.Add(entry.Name))
            {
                issues.Add(new VirtualPropertyIssue(entry.GetPath(), "The name is used twice at this level."));
            }

            FindDuplicateNames(entry.Properties, issues);
            FindDuplicateNames(entry.Attributes, issues);
        }
    }
}
