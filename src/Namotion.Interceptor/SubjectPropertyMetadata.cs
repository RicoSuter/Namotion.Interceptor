namespace Namotion.Interceptor;

public readonly struct SubjectPropertyMetadata
{
    public string Name { get; }
    
    public Type Type { get; }
    
    public Attribute[] Attributes { get; }
    
    public Func<object?, object?>? GetValue { get; }
    
    public Action<object?, object?>? SetValue { get; }

    public SubjectPropertyMetadata(
        string name, Type type, Attribute[] attributes, 
        Func<object?, object?>? getValue, Action<object?, object?>? setValue)
    {
        Name = name;
        Type = type;
        Attributes = attributes;
        GetValue = getValue;
        SetValue = setValue;
    }
}