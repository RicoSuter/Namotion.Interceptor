namespace HomeBlaze.Automation.Runtime;

/// <summary>The outcome of one action: the description of what ran, or the reason it failed.</summary>
internal readonly record struct ActionResult(bool IsSuccess, string Message)
{
    public static ActionResult Success(string description) => new(true, description);

    public static ActionResult Failure(string error) => new(false, error);
}
