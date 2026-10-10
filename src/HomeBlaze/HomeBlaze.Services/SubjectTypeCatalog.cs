using System.ComponentModel;
using System.Reflection;
using System.Text.RegularExpressions;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor;

namespace HomeBlaze.Services;

/// <summary>
/// A group of creatable subject types and the number of its types that match the current search.
/// </summary>
public sealed record SubjectTypeGroup(string Name, int Count);

/// <summary>
/// Lists the subject types that can be created, with the information the create dialog shows for them.
/// </summary>
public partial class SubjectTypeCatalog
{
    /// <summary>
    /// The group of types that have no <see cref="CategoryAttribute"/>.
    /// </summary>
    public const string OtherGroup = "Other";

    private const string DefaultIconName = "Category";

    private static readonly Dictionary<string, string> GroupIconNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Devices"] = "DevicesOther",
        ["Servers"] = "Dns",
        ["Clients"] = "Cable",
        ["History"] = "History",
        ["Layouts"] = "Dashboard",
        ["Components"] = "Widgets",
        ["Samples"] = "Science",
        ["Storage"] = "Storage"
    };

    private readonly SubjectTypeRegistry _typeRegistry;

    public SubjectTypeCatalog(SubjectTypeRegistry typeRegistry)
    {
        _typeRegistry = typeRegistry;
    }

    /// <summary>
    /// Gets the creatable types, ordered by group (alphabetically, <see cref="OtherGroup"/> last) and then by display name.
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
            .OrderBy(descriptor => descriptor.Group == OtherGroup)
            .ThenBy(descriptor => descriptor.Group, StringComparer.OrdinalIgnoreCase)
            .ThenBy(descriptor => descriptor.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Describes a type from its attributes, falling back to the class name split into words,
    /// the <see cref="OtherGroup"/> group and the icon of the group.
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
            displayName = WordBoundaryRegex().Replace(type.Name, " ");
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
    /// Returns the descriptors that match the search text and, when a group is given, belong to it. The order is preserved.
    /// </summary>
    public static IReadOnlyList<SubjectTypeDescriptor> Filter(
        IReadOnlyList<SubjectTypeDescriptor> descriptors, string? searchText, string? group)
    {
        return descriptors
            .Where(descriptor => group is null || string.Equals(descriptor.Group, group, StringComparison.OrdinalIgnoreCase))
            .Where(descriptor => descriptor.Matches(searchText))
            .ToList();
    }

    /// <summary>
    /// Returns every group in the order of the descriptors, with the number of its types that match the search text.
    /// </summary>
    public static IReadOnlyList<SubjectTypeGroup> GetGroups(IReadOnlyList<SubjectTypeDescriptor> descriptors, string? searchText)
    {
        return descriptors
            .GroupBy(descriptor => descriptor.Group)
            .Select(group => new SubjectTypeGroup(group.Key, group.Count(descriptor => descriptor.Matches(searchText))))
            .ToList();
    }

    /// <summary>
    /// Returns whether an existing name equals the name, with or without the suffix, ignoring case.
    /// </summary>
    public static bool IsNameTaken(string name, IReadOnlyCollection<string> existingNames, string suffix)
    {
        var nameWithSuffix = name + suffix;
        return existingNames.Any(existingName =>
            string.Equals(existingName, name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(existingName, nameWithSuffix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Suggests a name for a new subject of the type: the class name, with a number from 2 appended while it is taken.
    /// </summary>
    public static string SuggestName(Type type, IReadOnlyCollection<string> existingNames, string suffix)
    {
        var name = type.Name;
        for (var number = 2; IsNameTaken(name, existingNames, suffix); number++)
        {
            name = type.Name + number;
        }

        return name;
    }

    [GeneratedRegex("(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])")]
    private static partial Regex WordBoundaryRegex();
}
