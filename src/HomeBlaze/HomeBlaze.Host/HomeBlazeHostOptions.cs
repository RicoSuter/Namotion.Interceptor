namespace HomeBlaze.Host;

/// <summary>Components that other libraries add to the layout of the user interface, without the host referencing them.</summary>
public sealed class HomeBlazeHostOptions
{
    /// <summary>Components without parameters shown at the end of the top bar, before its menu, in this order.</summary>
    public IList<Type> AppBarEndComponents { get; } = new List<Type>();

    /// <summary>Components without parameters shown above the content of every page of the main layout, in this order.</summary>
    public IList<Type> BannerComponents { get; } = new List<Type>();
}
