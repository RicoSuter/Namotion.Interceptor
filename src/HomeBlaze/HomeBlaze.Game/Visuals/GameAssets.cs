namespace HomeBlaze.Game.Visuals;

/// <summary>
/// Builds the URLs of the static web assets that visuals reference.
/// </summary>
public static class GameAssets
{
    /// <summary>
    /// The start of every asset URL a visual may reference.
    /// </summary>
    public const string ContentPrefix = "_content/";

    /// <summary>
    /// Gets the URL of a file under <c>wwwroot/</c> of the assembly that defines <paramref name="type"/>, so the URL
    /// follows the type when it moves to another library.
    /// </summary>
    /// <param name="type">A type of the assembly that ships the file.</param>
    /// <param name="path">The path below <c>wwwroot/</c>, for example "objects/nature/models/pine_tree_1.glb".</param>
    public static string Url(Type type, string path)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentException.ThrowIfNullOrEmpty(path);
        return $"{ContentPrefix}{type.Assembly.GetName().Name}/{path}";
    }
}
