namespace HomeBlaze.Components;

/// <summary>The structure actions that the block of a widget allows.</summary>
/// <param name="CanDelete">Whether the block can be deleted.</param>
/// <param name="CanMoveUp">Whether the block can move past the item before it.</param>
/// <param name="CanMoveDown">Whether the block can move past the item after it.</param>
/// <param name="CanEditText">Whether the block is text that <see cref="PageStructureEditor.EditTextAsync"/> edits.</param>
public readonly record struct PageBlockActions(bool CanDelete, bool CanMoveUp, bool CanMoveDown, bool CanEditText = false);
