namespace Namotion.Interceptor.Tracking.Change;

public static class SubjectMutationContext
{
    private static readonly AsyncLocal<DateTimeOffset?> CurrentTimestamp = new();
    
    public static void SetCurrentTimestamp(DateTimeOffset timestamp)
    {
        CurrentTimestamp.Value = timestamp;
    }
    
    public static void ResetCurrentTimestamp()
    {
        CurrentTimestamp.Value = null;
    }
    
    public static DateTimeOffset GetCurrentTimestamp()
    {
        return CurrentTimestamp.Value ?? DateTimeOffset.Now;
    }
}