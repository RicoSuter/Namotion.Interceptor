namespace HomeBlaze.Host.Services.Display;

/// <summary>
/// The values and methods of a subject that share a category.
/// </summary>
public sealed class SubjectMemberCategory<TValue, TMethod>
{
    private readonly List<TValue> _values = [];
    private readonly List<TMethod> _methods = [];

    internal SubjectMemberCategory(string name)
    {
        Name = name;
    }

    /// <summary>
    /// The name of the category.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// The values of the category, in the order they were given in.
    /// </summary>
    public IReadOnlyList<TValue> Values => _values;

    /// <summary>
    /// The methods of the category, in the order they were given in.
    /// </summary>
    public IReadOnlyList<TMethod> Methods => _methods;

    internal int LowestValuePosition { get; private set; } = int.MaxValue;

    internal int LowestMethodPosition { get; private set; } = int.MaxValue;

    internal void AddValue(TValue value, int position)
    {
        _values.Add(value);
        LowestValuePosition = Math.Min(LowestValuePosition, position);
    }

    internal void AddMethod(TMethod method, int position)
    {
        _methods.Add(method);
        LowestMethodPosition = Math.Min(LowestMethodPosition, position);
    }
}
