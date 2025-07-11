﻿namespace Namotion.Interceptor;

public readonly record struct SubjectPropertyMetadata
{
    public string Name { get; }
    
    public Type Type { get; }
    
    public IReadOnlyCollection<Attribute> Attributes { get; }
    
    public Func<IInterceptorSubject, object?>? GetValue { get; }
    
    public Action<IInterceptorSubject, object?>? SetValue { get; }
    
    public bool IsDynamic { get; }

    public SubjectPropertyMetadata(
        string name, Type type, IReadOnlyCollection<Attribute> attributes, 
        Func<IInterceptorSubject, object?>? getValue, Action<IInterceptorSubject, object?>? setValue,
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