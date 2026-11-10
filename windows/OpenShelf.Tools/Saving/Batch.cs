namespace OpenShelf.Tools;

/// <summary>Runs a save over several files the way the Mac editors do: files that would not change are counted and left alone,
/// and a failure stops the batch but reports what was already written.</summary>
public static class Batch
{
    /// <summary>
    /// Runs <paramref name="saveOne"/> for each item. It returns the path it wrote, or null when the item needed nothing
    /// (counted as unchanged).
    /// </summary>
    public static SaveOutcome Run<T>(IEnumerable<T> items, SaveOutcome outcome, Func<T, string?> saveOne,
        IProgress<double>? progress = null, CancellationToken cancellation = default)
    {
        var list = items as IReadOnlyList<T> ?? items.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                var saved = saveOne(list[i]);
                if (saved is null) outcome.Unchanged++;
                else outcome.Saved.Add(saved);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (SaveException e)
            {
                throw new SaveException(e.Kind, e.Message, e) { SavedBefore = outcome.Saved.ToList() };
            }
            catch (Exception e)
            {
                throw new SaveException(SaveException.Reason.Failed, e.Message, e) { SavedBefore = outcome.Saved.ToList() };
            }
            progress?.Report((i + 1.0) / list.Count);
        }
        return outcome;
    }
}
