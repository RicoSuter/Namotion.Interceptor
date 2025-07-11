﻿namespace Namotion.Interceptor;

public record struct PropertyReference
{
    private const string MetadataOverrideKey = "Namotion.Interceptor.Metadata";

    private SubjectPropertyMetadata? _metadata = null;

    public PropertyReference(IInterceptorSubject subject, string name)
    {
        Subject = subject;
        Name = name;
    }

    public IInterceptorSubject Subject { get; }
    
    public string Name { get; }
    
    public SubjectPropertyMetadata Metadata
    {
        get
        {
            if (_metadata is not null)
            {
                return _metadata.Value;
            }

            _metadata = 
                TryGetPropertyMetadata(out var md1) ? md1 : // dynamic metadata (overrides)
                Subject.Properties.TryGetValue(Name, out var md2) ? md2 : // static metadata
                throw new InvalidOperationException("No metadata found.");

            return _metadata!.Value;
        }
    }

    public object? GetValue()
    {
        return Metadata.GetValue?.Invoke(Subject);
    }
    
    public void SetValue(object? value)
    {
        Metadata.SetValue?.Invoke(Subject, value);
    }

    public void SetPropertyData(string key, object? value)
    {
        Subject.Data[$"{Name}:{key}"] = value;
    }

    public bool TryGetPropertyData(string key, out object? value)
    {
        return Subject.Data.TryGetValue($"{Name}:{key}", out value);
    }

    public object? GetPropertyData(string key)
    {
        return Subject.Data[$"{Name}:{key}"];
    }

    public T GetOrAddPropertyData<T>(string key, Func<T> valueFactory)
    {
        return (T)Subject.Data.GetOrAdd($"{Name}:{key}", _ => valueFactory())!;
    }

    public bool TryGetPropertyMetadata(out SubjectPropertyMetadata? propertyMetadata)
    {
        propertyMetadata = 
            TryGetPropertyData(MetadataOverrideKey, out var value) && 
            value is SubjectPropertyMetadata resultPropertyMetadata ? resultPropertyMetadata : null;

        return propertyMetadata is not null;
    }
    
    public void SetPropertyMetadata(SubjectPropertyMetadata propertyMetadata)
    {
        SetPropertyData(MetadataOverrideKey, propertyMetadata);
    }
}