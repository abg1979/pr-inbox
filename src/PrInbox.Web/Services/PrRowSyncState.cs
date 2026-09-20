using PrInbox.Core.Models;

namespace PrInbox.Web.Services;

/// <summary>
/// Four-state lifecycle summary for a single inbox row, used to color the
/// row and to gate the Review button. Centralized here so Inbox and My PRs
/// resolve identically and cannot drift.
/// </summary>
public enum PrRowSyncState
{
    /// <summary>This PR is the active target of a sync/enrich pass right
    /// now. Overrides every other state regardless of enrichment status.</summary>
    Running,

    /// <summary>Fully enriched (<see cref="EnrichState.Enriched"/>): the
    /// current detailed snapshot is ready and Review can be launched.</summary>
    ReviewReady,

    /// <summary><see cref="EnrichState.Basic"/> with an existing (older)
    /// snapshot: list-tier data is current but detailed enrichment for the
    /// latest upstream state hasn't landed yet. Review stays disabled.</summary>
    BasicComplete,

    /// <summary><see cref="EnrichState.Basic"/> with no snapshot yet — the
    /// row is waiting for its first detailed enrichment pass.</summary>
    Pending,
}

/// <summary>
/// Resolves a row's <see cref="PrRowSyncState"/> and the presentation
/// details (CSS class, label/tooltip, Review-enabled) that follow from it.
/// </summary>
public static class PrRowSyncStateResolver
{
    /// <summary>
    /// Resolve the row's state from its live activity flag plus its
    /// persisted enrichment inputs. Priority: an active sync always wins;
    /// otherwise enrichment + snapshot existence decide.
    /// </summary>
    public static PrRowSyncState Resolve(bool isActive, EnrichState enrichState, bool hasSnapshot)
    {
        if (isActive) return PrRowSyncState.Running;
        if (enrichState == EnrichState.Enriched) return PrRowSyncState.ReviewReady;
        return hasSnapshot ? PrRowSyncState.BasicComplete : PrRowSyncState.Pending;
    }

    /// <summary>CSS class applied to the row's <c>&lt;tr&gt;</c> for tinting/border.</summary>
    public static string CssClass(PrRowSyncState state) => state switch
    {
        PrRowSyncState.Running => "row-sync-running",
        PrRowSyncState.ReviewReady => "row-sync-ready",
        PrRowSyncState.BasicComplete => "row-sync-basic",
        PrRowSyncState.Pending => "row-sync-pending",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    /// <summary>Short accessible label describing the state (used as a title/tooltip
    /// and so the meaning isn't conveyed by color alone).</summary>
    public static string Label(PrRowSyncState state) => state switch
    {
        PrRowSyncState.Running => "Syncing now",
        PrRowSyncState.ReviewReady => "Review ready",
        PrRowSyncState.BasicComplete => "Basic sync complete — detailed sync pending",
        PrRowSyncState.Pending => "Sync pending",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    /// <summary>Tooltip explaining why Review is enabled/disabled for this state.</summary>
    public static string ReviewTooltip(PrRowSyncState state) => state switch
    {
        PrRowSyncState.Running => "This PR is syncing now — try again once it finishes.",
        PrRowSyncState.ReviewReady => "Launch review.",
        PrRowSyncState.BasicComplete => "Basic sync is complete, but detailed sync is still pending. Wait for detailed sync to launch review.",
        PrRowSyncState.Pending => "Waiting for the first detailed sync of this PR.",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    /// <summary>True only when the row is fully enriched and not mid-sync.</summary>
    public static bool ReviewEnabled(PrRowSyncState state) => state == PrRowSyncState.ReviewReady;
}
