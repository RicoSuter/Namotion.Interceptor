namespace HomeBlaze.Automation;

/// <summary>What a rule does after one of its actions failed. Neither policy undoes an action that succeeded.</summary>
public enum ActionErrorPolicy
{
    /// <summary>The remaining actions run.</summary>
    Continue,

    /// <summary>The remaining actions of the rule are skipped.</summary>
    StopRule
}
