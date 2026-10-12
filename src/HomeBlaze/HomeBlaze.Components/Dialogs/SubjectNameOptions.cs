using System.Buffers;

namespace HomeBlaze.Components.Dialogs;

/// <summary>
/// How the create subject dialog asks for the name of the new subject.
/// </summary>
/// <param name="Label">The label of the name field, which the error messages also name the field by.</param>
/// <param name="Suffix">A fixed suffix shown after the name, such as ".json", or an empty string.</param>
/// <param name="ExistingNames">The names that are already used, with or without the suffix. The dialog rejects them.</param>
public sealed record SubjectNameOptions(string Label, string Suffix, IReadOnlyCollection<string> ExistingNames)
{
    private static readonly SearchValues<char> InvalidFileNameCharacters =
        SearchValues.Create(Path.GetInvalidFileNameChars());

    /// <summary>
    /// A rule that a name must fit in addition, or null when there is none. It gets the name without surrounding
    /// whitespace and returns the error message, or null when the name fits.
    /// </summary>
    public Func<string, string?>? Rule { get; init; }

    /// <summary>
    /// Validates a name, ignoring surrounding whitespace: it must not be empty, must fit the <see cref="Rule"/>,
    /// must be usable as a file name and must not equal an existing name, with or without the suffix, ignoring case.
    /// </summary>
    /// <returns>The error message, or null when the name is valid.</returns>
    public string? Validate(string? name)
    {
        var trimmedName = (name ?? "").Trim();
        if (trimmedName.Length == 0)
        {
            return $"{Label} is required";
        }

        // Before the check for a file name, so that a name with a character that neither allows gets the message
        // that says what a valid name is.
        if (Rule?.Invoke(trimmedName) is { } ruleError)
        {
            return ruleError;
        }

        if (trimmedName.AsSpan().ContainsAny(InvalidFileNameCharacters))
        {
            return $"{Label} contains invalid characters";
        }

        if (IsTaken(trimmedName))
        {
            return $"This {GetLabelWithinSentence()} is already used";
        }

        return null;
    }

    /// <summary>
    /// Suggests a name for a new subject of the type: the class name, with a number from 2 appended while it is taken.
    /// </summary>
    public string SuggestName(Type type)
    {
        var name = type.Name;
        for (var number = 2; IsTaken(name); number++)
        {
            name = type.Name + number;
        }

        return name;
    }

    private bool IsTaken(string name)
    {
        var nameWithSuffix = name + Suffix;
        foreach (var existingName in ExistingNames)
        {
            if (string.Equals(existingName, name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(existingName, nameWithSuffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private string GetLabelWithinSentence()
    {
        // A label that starts with an acronym, such as "URL name", keeps its capitals.
        var startsWithAcronym = Label.Length > 1 && char.IsUpper(Label[1]);
        return Label.Length == 0 || startsWithAcronym
            ? Label
            : char.ToLowerInvariant(Label[0]) + Label[1..];
    }
}
