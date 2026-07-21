namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// Copy-on-write helpers for the immutable subscription arrays; callers publish the returned array atomically.
/// </summary>
internal static class CopyOnWriteArray
{
    public static T[] Add<T>(T[] array, T item)
    {
        var updated = new T[array.Length + 1];
        Array.Copy(array, updated, array.Length);
        updated[array.Length] = item;
        return updated;
    }

    public static T[] RemoveAt<T>(T[] array, int index)
    {
        var updated = new T[array.Length - 1];
        Array.Copy(array, 0, updated, 0, index);
        Array.Copy(array, index + 1, updated, index, array.Length - index - 1);
        return updated;
    }
}
