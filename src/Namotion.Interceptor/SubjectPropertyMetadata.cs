namespace Namotion.Interceptor;

public readonly record struct SubjectPropertyMetadata
{
    public string Name { get; }
    
    public Type Type { get; }
    
    public IReadOnlyCollection<Attribute> Attributes { get; }
    
    public Func<object, object?>? GetValue { get; }
    
    public Action<object, object?>? SetValue { get; }
    
    public bool IsDynamic { get; }

    public SubjectPropertyMetadata(
        string name, Type type, IReadOnlyCollection<Attribute> attributes, 
        Func<object, object?>? getValue, Action<object, object?>? setValue,
        bool isDynamic)
    {
        Name = name;
        Type = type;
        Attributes = attributes;
        GetValue = getValue;
        SetValue = setValue;
        IsDynamic = isDynamic;
    }
}