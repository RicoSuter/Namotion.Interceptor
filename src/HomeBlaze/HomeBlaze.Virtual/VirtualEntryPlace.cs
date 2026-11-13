namespace HomeBlaze.Virtual;

/// <summary>Where an entry of the properties of a virtual subject is.</summary>
public enum VirtualEntryPlace
{
    /// <summary>An entry of the virtual subject itself.</summary>
    Root,

    /// <summary>An entry of a nested subject.</summary>
    Nested,

    /// <summary>An attribute of a property.</summary>
    Attribute
}
