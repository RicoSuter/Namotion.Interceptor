namespace HomeBlaze.Components;

/// <summary>The structure actions that the block of a widget allows.</summary>
/// <param name="CanDelete">Whether the block can be deleted.</param>
/// <param name="CanMoveUp">Whether the block can move past the item before it.</param>
/// <param name="CanMoveDown">Whether the block can move past the item after it.</param>
/// <param name="CanEditText">Whether the block is text that <see cref="PageStructureEditor.EditTextAsync"/> edits.</param>
/// <param name="CanEditInSource">
/// Whether the block is content that cannot be edited inline, which <see cref="PageStructureEditor.EditInSourceAsync"/> shows
/// in the source of the page.
/// </param>
public readonly record struct PageBlockActions(
    bool CanDelete, bool CanMoveUp, bool CanMoveDown, bool CanEditText = false, bool CanEditInSource = false);
