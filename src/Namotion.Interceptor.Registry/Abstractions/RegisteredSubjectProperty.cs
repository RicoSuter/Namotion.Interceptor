using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Interceptor.Registry.Abstractions;

#pragma warning disable CS8618
public record RegisteredSubjectProperty(PropertyReference Property)
#pragma warning restore CS8618
{
    private readonly HashSet<SubjectPropertyChild> _children = new();

    public required Type Type { get; init; }

    public required IReadOnlyCollection<Attribute> Attributes { get; init; }
    
    public bool IsAttribute => Attributes.Any(a => a is PropertyAttributeAttribute);
    
    public string BrowseName => IsAttribute ? Attribute.AttributeName : Property.Name;
    
    // TODO(perf): Cache the attribute property name
    public PropertyAttributeAttribute Attribute => Attributes.OfType<PropertyAttributeAttribute>().Single();

#pragma warning disable CS8618
    public RegisteredSubject Parent { get; internal set; }
#pragma warning restore CS8618

    public virtual bool HasGetter => Property.Metadata.GetValue is not null;

    public virtual bool HasSetter => Property.Metadata.SetValue is not null;

    public bool HasPropertyAttributes(string propertyName) 
        => Attributes.OfType<PropertyAttributeAttribute>().Any(a => a.PropertyName == propertyName);

    public virtual object? GetValue()
    {
        return Property.Metadata.GetValue?.Invoke(Property.Subject);
    }

    public virtual void SetValue(object? value)
    {
        Property.Metadata.SetValue?.Invoke(Property.Subject, value);
    }

    public ICollection<SubjectPropertyChild> Children
    {
        get
        {
            lock (this)
            {
                return _children.ToArray();
            }
        }
    }

    public void AddChild(SubjectPropertyChild parent)
    {
        lock (this)
            _children.Add(parent);
    }

    public void RemoveChild(SubjectPropertyChild parent)
    {
        lock (this)
            _children.Remove(parent);
    }

    public string AddAttribute(string name, Type type, 
        Func<object?>? getValue, Action<object?>? setValue, 
        params Attribute[] attributes)
    {
        var propertyName = $"{Property.Name}@{name}";
        
        Parent.AddProperty(
            propertyName,
            type, getValue, setValue,
            attributes.Concat([new PropertyAttributeAttribute(Property.Name, name)]).ToArray());

        return propertyName;
    }
}
