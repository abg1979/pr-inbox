namespace PrInbox.Web.Services;

public enum InboxSortColumn
{
    None,
    Source,
    Repo,
    Title,
    Author,
    Threads,
    State,
    Approvals,
    Age,
    LastSync,
    Review,
}

public sealed record InboxTableSort(InboxSortColumn Column = InboxSortColumn.None, bool Descending = false)
{
    public InboxTableSort Next(InboxSortColumn column) =>
        Column != column ? new(column) :
        !Descending ? new(column, true) : new();

    public static InboxTableSort FromLegacyAge(AgeSortMode mode) => mode switch
    {
        AgeSortMode.OldestFirst => new(InboxSortColumn.Age),
        AgeSortMode.NewestFirst => new(InboxSortColumn.Age, true),
        _ => new(),
    };

    public IReadOnlyList<InboxRow> Order(
        IReadOnlyList<InboxRow> rows,
        Func<InboxRow, string> sourceLabel,
        IReadOnlyDictionary<string, ReviewRun> runs)
    {
        // Partition missing values before applying direction so they always remain last.
        IOrderedEnumerable<InboxRow> By<T>(Func<InboxRow, T> key, IComparer<T>? comparer = null)
        {
            var knownFirst = rows.OrderBy(row => key(row) is null);
            return Descending
                ? knownFirst.ThenByDescending(key, comparer)
                : knownFirst.ThenBy(key, comparer);
        }

        (int, int, int, int, int)? ReviewKey(InboxRow row) =>
            !runs.TryGetValue(row.Url, out var run) ? null :
            (run.Findings is null ? -1 : 0,
                run.CriticalCount, run.HighCount, run.MediumCount, run.LowCount);

        switch (Column)
        {
            case InboxSortColumn.Source:
                return By(sourceLabel, StringComparer.OrdinalIgnoreCase).ToList();
            case InboxSortColumn.Repo:
                var byRepo = By(row => row.DisplayRepo, StringComparer.OrdinalIgnoreCase);
                return (Descending
                    ? byRepo.ThenByDescending(row => row.Number)
                    : byRepo.ThenBy(row => row.Number)).ToList();
            case InboxSortColumn.Title:
                return By(row => row.Title, StringComparer.OrdinalIgnoreCase).ToList();
            case InboxSortColumn.Author:
                return By(row => row.AuthorLogin, StringComparer.OrdinalIgnoreCase).ToList();
            case InboxSortColumn.Threads:
                return By(row => row.OpenThreadCount).ToList();
            case InboxSortColumn.State:
                return By(row => row.Status.ToString(), StringComparer.OrdinalIgnoreCase).ToList();
            case InboxSortColumn.Approvals:
                return By(row => row.ApprovalCount).ToList();
            case InboxSortColumn.Age:
                return InboxAgeSort.Order(rows, Descending ? AgeSortMode.NewestFirst : AgeSortMode.OldestFirst);
            case InboxSortColumn.LastSync:
                return By(row => row.LastSyncedAt).ToList();
            case InboxSortColumn.Review:
                return By(ReviewKey).ToList();
            default:
                return rows;
        }
    }
}
