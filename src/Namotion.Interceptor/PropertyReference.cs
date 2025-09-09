namespace Namotion.Interceptor;

public record struct PropertyReference
{
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
                Subject.Properties.TryGetValue(Name, out var metadata) ? metadata :
                throw new InvalidOperationException("No metadata found.");

            return _metadata!.Value;
        }
    }

    public void SetPropertyData(string key, object? value)
    {
        Subject.Data[$"{Name}:{key}"] = value;
    }

    public bool TryGetPropertyData(string key, out object? value)
    {
        return Subject.Data.TryGetValue($"{Name}:{key}", out value);
    }
}