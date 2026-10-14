namespace HomeBlaze.Storage.Pages;

/// <summary>What one change of the page text did to its lines.</summary>
/// <param name="Line">The 1-based line where the change starts: the first removed line, or the line of the first inserted line.</param>
/// <param name="RemovedLineCount">How many lines were removed from <paramref name="Line"/> on.</param>
/// <param name="InsertedLineCount">How many lines were inserted at <paramref name="Line"/>.</param>
internal readonly record struct MarkdownLineEdit(int Line, int RemovedLineCount, int InsertedLineCount);
