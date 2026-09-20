using PrInbox.Core.Models;
using PrInbox.Core.Storage;
using PrInbox.Sources;
using PrInbox.Sources.Fakes;

namespace PrInbox.Tests.Sources;

/// <summary>
/// Locks the contract that <see cref="SyncOrchestrator"/> populates
/// <see cref="SyncProgress.PrUrl"/> for every per-PR progress event (fast
/// list + enrich) but leaves it <c>null</c> for phase-level events. The
/// Inbox/My PRs "syncing" row indicator (<c>InboxState.GetActiveRowActivity</c>)
/// depends entirely on this distinction: a non-null <c>PrUrl</c> lights up
/// exactly one row; a null one must never masquerade as row-specific
/// activity.
/// </summary>
public class SyncProgressUrlTests : IAsyncLifetime
{
    private string _connString = string.Empty;
    private PrInboxDb _db = null!;
    private Microsoft.Data.Sqlite.SqliteConnection _keepAlive = null!;

    private PullRequestRepository _prs = null!;
    private PrSnapshotRepository _snaps = null!;
    private ObservedThreadRepository _threads = null!;
    private SyncRunRepository _syncRuns = null!;

    public async Task InitializeAsync()
    {
        _connString = PrInboxDb.InMemoryConnectionString($"progress-url-{Guid.NewGuid():N}");
        _db = new PrInboxDb(_connString);
        _keepAlive = await _db.OpenAsync();
        await new MigrationRunner().MigrateAsync(_connString);

        _prs = new PullRequestRepository(_db);
        _snaps = new PrSnapshotRepository(_db);
        _threads = new ObservedThreadRepository(_db);
        _syncRuns = new SyncRunRepository(_db);
    }

    public Task DisposeAsync() => _keepAlive.DisposeAsync().AsTask();

    [Fact]
    public async Task RunFast_Per_Pr_Events_Carry_The_Pr_Url()
    {
        var idAlpha = new PrIdentity("https://github.com/owner/repo/pull/1", "gh.com:100#1000");
        var idBeta = new PrIdentity("https://github.com/owner/repo/pull/2", "gh.com:100#2000");
        var source = new FakePrReadSourceBuilder("gh.com:emu", SourceKind.GitHub)
            .WithPullRequest(BuildBasicPr(idAlpha), BuildDetail(idAlpha))
            .WithPullRequest(BuildBasicPr(idBeta), BuildDetail(idBeta))
            .Build();
        var orch = new SyncOrchestrator(source, _prs, _snaps, _threads, _syncRuns);

        var events = new List<SyncProgress>();
        var progress = new RecordingProgress(events.Add);

        await orch.RunFastAsync("jmprieur_microsoft", progress, CancellationToken.None);

        // The first event ("Fetching inbox") is phase-level: no PR yet.
        events.First().PrUrl.Should().BeNull();

        // Every per-PR event must carry that PR's URL.
        var perPrEvents = events.Where(e => e.PrUrl is not null).ToList();
        perPrEvents.Should().HaveCount(2);
        perPrEvents.Select(e => e.PrUrl).Should().BeEquivalentTo(new[] { idAlpha.Url, idBeta.Url });
    }

    [Fact]
    public async Task RunEnrich_Header_Event_Has_No_Pr_Url_But_Per_Pr_Events_Do()
    {
        var idAlpha = new PrIdentity("https://github.com/owner/repo/pull/1", "gh.com:100#1000");
        var source = new FakePrReadSourceBuilder("gh.com:emu", SourceKind.GitHub)
            .WithPullRequest(BuildBasicPr(idAlpha), BuildDetail(idAlpha))
            .Build();
        var orch = new SyncOrchestrator(source, _prs, _snaps, _threads, _syncRuns);
        await orch.RunFastAsync("jmprieur_microsoft", progress: null, CancellationToken.None);

        var events = new List<SyncProgress>();
        var progress = new RecordingProgress(events.Add);

        await orch.RunEnrichAsync("jmprieur_microsoft", progress, CancellationToken.None);

        // Header event ("Enriching: N PR(s)") is phase-level.
        events.First().PrUrl.Should().BeNull();

        // The per-row event must carry the enriched PR's URL.
        var perPrEvents = events.Where(e => e.PrUrl is not null).ToList();
        perPrEvents.Should().ContainSingle();
        perPrEvents[0].PrUrl.Should().Be(idAlpha.Url);
    }

    private sealed class RecordingProgress : IProgress<SyncProgress>
    {
        private readonly Action<SyncProgress> _onReport;
        public RecordingProgress(Action<SyncProgress> onReport) => _onReport = onReport;
        public void Report(SyncProgress value) => _onReport(value);
    }

    private static RemotePullRequest BuildBasicPr(PrIdentity id) =>
        new(
            Identity: id,
            SourceKind: SourceKind.GitHub,
            SourceId: "gh.com:emu",
            DisplayRepo: "owner/repo",
            Number: int.Parse(id.Url[(id.Url.LastIndexOf('/') + 1)..]),
            Title: "Title",
            AuthorLogin: "author",
            Url: id.Url,
            Status: PullRequestStatus.Open,
            LastUpdated: DateTimeOffset.Parse("2026-05-13T10:00:00Z"),
            CreatedAt: DateTimeOffset.Parse("2026-05-01T10:00:00Z"),
            IsDraft: false);

    private static RemotePullRequestDetail BuildDetail(PrIdentity id) =>
        new(
            Identity: id,
            HeadSha: "deadbeef00000000",
            BaseSha: "0000000000000000",
            MergeBaseSha: "0000000000000000",
            OrderedCommitShas: new[] { "deadbeef00000000" },
            ReviewerState: ReviewerState.Waiting,
            Status: PullRequestStatus.Open,
            RawMetadataJson: "{}",
            MergeableState: "clean",
            CiStatus: "success",
            Files: Array.Empty<RemoteFileChange>(),
            ReviewDecision: null,
            Body: null,
            IsDraft: false);
}
