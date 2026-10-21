namespace HomeBlaze.Components;

/// <summary>The structure actions that the block of a widget allows.</summary>
public readonly record struct PageBlockActions(bool CanDelete, bool CanMoveUp, bool CanMoveDown);
