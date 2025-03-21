namespace Namotion.Interceptor.Registry.Abstractions;

/// <summary>
/// A dynamic registered subject property which is not part of compilation.
/// </summary>
internal record DynamicRegisteredSubjectProperty : RegisteredSubjectProperty
{
    private readonly Func<object?>? _getValue;
    private readonly Action<object?>? _setValue;

    public DynamicRegisteredSubjectProperty(PropertyReference property, Func<object?>? getValue, Action<object?>? setValue)
        : base(property)
    {
        _getValue = getValue;
        _setValue = setValue;
    }

    public override bool HasGetter => _getValue is not null;

    public override bool HasSetter => _setValue is not null;

    public override object? GetValue()
    {
        return _getValue?.Invoke();
    }

    public override void SetValue(object? value)
    {
        _setValue?.Invoke(value);
    }
}
