using System.ComponentModel;
using System.Reflection;
using System.Text.RegularExpressions;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor;

namespace HomeBlaze.Services;

/// <summary>
/// Lists the subject types that can be created, with the information the create dialog shows for them.
/// </summary>
public partial class SubjectTypeCatalog
{
    /// <summary>
    /// The group of types that have no <see cref="CategoryAttribute"/>.
    /// </summary>
    public const string OtherGroup = "Other";

    private static readonly StringComparer GroupComparer = StringComparer.OrdinalIgnoreCase;

    private const string DefaultIconName = "Category";

    internal static readonly Dictionary<string, string> GroupIconNames = new(GroupComparer)
    {
        ["Integrations"] = "SettingsEthernet",
        ["Hardware"] = "Memory",
        ["Switches"] = "ToggleOn",
        ["Energy"] = "Bolt",
        ["Climate"] = "Thermostat",
        ["Lighting"] = "Lightbulb",
        ["Media"] = "Speaker",
        ["Dashboard"] = "Dashboard",
        ["History"] = "History",
        ["System"] = "Settings",
        ["Samples"] = "Science"
    };

    private readonly SubjectTypeRegistry _typeRegistry;

    public SubjectTypeCatalog(SubjectTypeRegistry typeRegistry)
    {
        _typeRegistry = typeRegistry;
    }

    /// <summary>
    /// Gets the creatable types, ordered by group (alphabetically ignoring case, <see cref="OtherGroup"/> last) and then by display name.
    /// </summary>
    /// <param name="existingSubjects">The subjects of the current graph, used to hide single instance types that already exist.</param>
    public IReadOnlyList<SubjectTypeDescriptor> GetCreatableTypes(IEnumerable<IInterceptorSubject> existingSubjects)
    {
        var existingTypes = existingSubjects.Select(subject => subject.GetType()).ToHashSet();

        return _typeRegistry.RegisteredTypes
            .Where(type => typeof(IConfigurable).IsAssignableFrom(type) && !type.IsAbstract)
            .Where(type => !type.IsDefined(typeof(ExcludeFromCreationAttribute), false))
            .Where(type => !type.IsDefined(typeof(SingleInstanceAttribute), false) || !existingTypes.Contains(type))
            .Select(Describe)
            .OrderBy(descriptor => GroupComparer.Equals(descriptor.Group, OtherGroup))
            .ThenBy(descriptor => descriptor.Group, GroupComparer)
            .ThenBy(descriptor => descriptor.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Describes a type from the attributes it declares itself, falling back to the class name split into words
    /// in sentence case, the <see cref="OtherGroup"/> group and the icon of the group.
    /// </summary>
    public static SubjectTypeDescriptor Describe(Type type)
    {
        var group = type.GetCustomAttribute<CategoryAttribute>(false)?.Category;
        if (string.IsNullOrWhiteSpace(group))
        {
            group = OtherGroup;
        }

        var displayName = type.GetCustomAttribute<DisplayNameAttribute>(false)?.DisplayName;
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = GetFallbackDisplayName(type.Name);
        }

        var iconName = type.GetCustomAttribute<SubjectIconAttribute>(false)?.IconName;
        if (string.IsNullOrWhiteSpace(iconName))
        {
            iconName = GroupIconNames.GetValueOrDefault(group, DefaultIconName);
        }

        var description = type.GetCustomAttribute<DescriptionAttribute>(false)?.Description;
        if (string.IsNullOrWhiteSpace(description))
        {
            description = null;
        }

        return new SubjectTypeDescriptor(type, displayName, description, group, iconName);
    }

    /// <summary>
    /// Returns every group in the order of the descriptors, named after its first descriptor, with those of its
    /// descriptors that match the search text, in their order. A group without a match is still returned.
    /// </summary>
    /// <param name="descriptors">The descriptors to group.</param>
    /// <param name="searchText">Whitespace separated terms that a match must all contain, or nothing to match every descriptor.</param>
    public static IReadOnlyList<SubjectTypeGroup> GetGroups(IReadOnlyList<SubjectTypeDescriptor> descriptors, string? searchText)
    {
        var searchTerms = (searchText ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        var groups = new List<SubjectTypeGroup>();
        var matchesByGroup = new Dictionary<string, List<SubjectTypeDescriptor>>(GroupComparer);
        foreach (var descriptor in descriptors)
        {
            if (!matchesByGroup.TryGetValue(descriptor.Group, out var matches))
            {
                matches = [];
                matchesByGroup.Add(descriptor.Group, matches);
                groups.Add(new SubjectTypeGroup(descriptor.Group, matches));
            }

            if (descriptor.Matches(searchTerms))
            {
                matches.Add(descriptor);
            }
        }

        return groups;
    }

    private static string GetFallbackDisplayName(string typeName)
    {
        var words = WordBoundaryRegex().Split(typeName);
        for (var index = 1; index < words.Length; index++)
        {
            var word = words[index];
            if (word.Any(char.IsLower))
            {
                words[index] = char.ToLowerInvariant(word[0]) + word[1..];
            }
        }

        return string.Join(' ', words);
    }

    [GeneratedRegex("(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])")]
    private static partial Regex WordBoundaryRegex();
}
