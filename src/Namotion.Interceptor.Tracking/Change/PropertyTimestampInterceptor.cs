using System.Runtime.CompilerServices;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// Interceptor that tracks the last changed timestamp for each property.
/// Used for conflict detection in transactions.
/// </summary>
public sealed class PropertyTimestampInterceptor : IWriteInterceptor
{
    internal const string LastChangedTimestampKey = "LastChangedTimestamp";

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
        next(ref context);

        var changeContext = SubjectChangeContext.Current;
        context.Property.SetPropertyData(LastChangedTimestampKey, changeContext.ChangedTimestamp);
    }
}
