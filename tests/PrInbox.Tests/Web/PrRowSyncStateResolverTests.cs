using PrInbox.Core.Models;
using PrInbox.Web.Services;
using System.Linq;
using Xunit;

namespace PrInbox.Tests.Web;

/// <summary>
/// Tests for <see cref="PrRowSyncStateResolver"/> — the shared four-state
/// row lifecycle model that drives both row tinting and Review-button
/// gating on Inbox and My PRs. Priority ordering (active sync overrides
/// everything else) and the Basic+snapshot vs Basic+no-snapshot split are
/// the two behaviors most likely to regress.
/// </summary>
public class PrRowSyncStateResolverTests
{
    [Fact]
    public void Active_sync_overrides_enriched()
    {
        var state = PrRowSyncStateResolver.Resolve(isActive: true, EnrichState.Enriched, hasSnapshot: true);
        Assert.Equal(PrRowSyncState.Running, state);
    }

    [Fact]
    public void Active_sync_overrides_basic_with_snapshot()
    {
        var state = PrRowSyncStateResolver.Resolve(isActive: true, EnrichState.Basic, hasSnapshot: true);
        Assert.Equal(PrRowSyncState.Running, state);
    }

    [Fact]
    public void Active_sync_overrides_basic_without_snapshot()
    {
        var state = PrRowSyncStateResolver.Resolve(isActive: true, EnrichState.Basic, hasSnapshot: false);
        Assert.Equal(PrRowSyncState.Running, state);
    }

    [Fact]
    public void Enriched_and_not_active_is_review_ready()
    {
        var state = PrRowSyncStateResolver.Resolve(isActive: false, EnrichState.Enriched, hasSnapshot: true);
        Assert.Equal(PrRowSyncState.ReviewReady, state);
    }

    [Fact]
    public void Enriched_is_review_ready_even_without_snapshot_flag()
    {
        // Enriched implies a snapshot exists in practice, but the resolver
        // should key off EnrichState first regardless of the flag value.
        var state = PrRowSyncStateResolver.Resolve(isActive: false, EnrichState.Enriched, hasSnapshot: false);
        Assert.Equal(PrRowSyncState.ReviewReady, state);
    }

    [Fact]
    public void Basic_with_snapshot_is_basic_complete_not_review_ready()
    {
        var state = PrRowSyncStateResolver.Resolve(isActive: false, EnrichState.Basic, hasSnapshot: true);
        Assert.Equal(PrRowSyncState.BasicComplete, state);
    }

    [Fact]
    public void Basic_without_snapshot_is_pending()
    {
        var state = PrRowSyncStateResolver.Resolve(isActive: false, EnrichState.Basic, hasSnapshot: false);
        Assert.Equal(PrRowSyncState.Pending, state);
    }

    [Theory]
    [InlineData(PrRowSyncState.Running, false)]
    [InlineData(PrRowSyncState.ReviewReady, true)]
    [InlineData(PrRowSyncState.BasicComplete, false)]
    [InlineData(PrRowSyncState.Pending, false)]
    public void ReviewEnabled_is_true_only_for_review_ready(PrRowSyncState state, bool expectedEnabled)
    {
        Assert.Equal(expectedEnabled, PrRowSyncStateResolver.ReviewEnabled(state));
    }

    [Fact]
    public void CssClass_and_label_are_distinct_per_state()
    {
        var states = new[]
        {
            PrRowSyncState.Running,
            PrRowSyncState.ReviewReady,
            PrRowSyncState.BasicComplete,
            PrRowSyncState.Pending,
        };

        var classes = states.Select(PrRowSyncStateResolver.CssClass).ToArray();
        var labels = states.Select(PrRowSyncStateResolver.Label).ToArray();
        var tooltips = states.Select(PrRowSyncStateResolver.ReviewTooltip).ToArray();

        Assert.Equal(classes.Length, classes.Distinct().Count());
        Assert.Equal(labels.Length, labels.Distinct().Count());
        Assert.Equal(tooltips.Length, tooltips.Distinct().Count());
        Assert.All(classes, c => Assert.False(string.IsNullOrWhiteSpace(c)));
        Assert.All(labels, l => Assert.False(string.IsNullOrWhiteSpace(l)));
        Assert.All(tooltips, t => Assert.False(string.IsNullOrWhiteSpace(t)));
    }
}
