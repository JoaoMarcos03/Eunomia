namespace Eunomia.Engine.Results;

/// <summary>
/// Full value of a cell, without truncation or display formatting (sections 4.4 and 4.6):
/// used when copying, editing or exporting. In the product it fetches the row by key; runs off the UI thread.
/// </summary>
public interface IFullValueSource
{
    /// <summary>Full text of the value, or <c>null</c> if it is NULL. Integers without a thousands separator.</summary>
    ValueTask<string?> GetFullTextAsync(long row, int column, CancellationToken cancellationToken = default);
}
