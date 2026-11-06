namespace HomeBlaze.Automation.Runtime;

/// <summary>The inputs that were dropped because the queue was full or because they continued a feedback chain too long.</summary>
internal readonly record struct QueueFaults(int Overloaded, int FeedbackChains);
