using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PrInbox.Core.Credentials;
using PrInbox.Core.Models;
using PrInbox.Core.Storage;
using PrInbox.Sources;
using PrInbox.Web.Services;

namespace PrInbox.Tests.Web;

/// <summary>
/// Locks the end-to-end wiring between <see cref="InboxSyncHostedService"/>'s
/// fast-sync fan-out and <see cref="InboxState"/>'s row-activity map: while a
/// source is mid-PR, that PR's URL must report an active "syncing" label,
/// and once the source's task ends (success or failure) its marker must be
/// cleared so no row is left stuck showing "syncing" forever.
/// </summary>
public class InboxSyncHostedServiceRowActivityTests : IAsyncLifetime
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
        _connString = PrInboxDb.InMemoryConnectionString($"row-activity-{Guid.NewGuid():N}");
        _db = new PrInboxDb(_connString);
        _keepAlive = await _db.OpenAsync();
        await new MigrationRunner().MigrateAsync(_connString);

        _prs = new PullRequestRepository(_db);
        _snaps = new PrSnapshotRepository(_db);
        _threads = new ObservedThreadRepository(_db);
        _syncRuns = new SyncRunRepository(_db);
    }

    public async Task DisposeAsync()
    {
        await _keepAlive.DisposeAsync();
    }

    [Fact]
    public async Task Pr_Shows_Active_While_In_Flight_And_Clears_After_Success()
    {
        var id = new PrIdentity("https://github.com/owner/repo/pull/1", "gh.com:emu:1#1000");
        var source = new BlockingAfterFirstFakeSource("gh.com:emu", id);
        var rt = new RuntimeSource(source, new StubTokenProvider(source.SourceId), "jmprieur_microsoft");

        var state = new InboxState();
        var host = NewHost(state);

        var runTask = host.RunFastSyncAsync(new[] { rt }, _prs, _snaps, _threads, _syncRuns, CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await source.Entered.Task.WaitAsync(cts.Token);

        // While the source is paused after yielding PR #1, the row must
        // show as actively syncing.
        state.GetActiveRowActivity(id.Url).Should().NotBeNull();

        source.Release.TrySetResult();
        var failures = await runTask;

        failures.Should().Be(0);
        // Once the task ends, the marker must be gone — no stale "syncing"
        // label left behind.
        state.GetActiveRowActivity(id.Url).Should().BeNull();
    }

    [Fact]
    public async Task Source_Failure_Still_Clears_The_Active_Row_Marker()
    {
        var id = new PrIdentity("https://github.com/owner/repo/pull/1", "gh.com:emu:1#1000");
        var source = new BlockingThenThrowFakeSource("gh.com:emu", id);
        var rt = new RuntimeSource(source, new StubTokenProvider(source.SourceId), "jmprieur_microsoft");

        var state = new InboxState();
        var host = NewHost(state);

        var runTask = host.RunFastSyncAsync(new[] { rt }, _prs, _snaps, _threads, _syncRuns, CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await source.Entered.Task.WaitAsync(cts.Token);
        state.GetActiveRowActivity(id.Url).Should().NotBeNull();

        source.Release.TrySetResult();
        var failures = await runTask;

        failures.Should().Be(1);
        state.GetActiveRowActivity(id.Url).Should().BeNull();
    }

    private static InboxSyncHostedService NewHost(InboxState state)
    {
        var config = new ConfigurationBuilder().Build();
        return new InboxSyncHostedService(
            state,
            config,
            NullLogger<InboxSyncHostedService>.Instance,
            NullLoggerFactory.Instance);
    }

    private sealed class StubTokenProvider : ITokenProvider
    {
        public StubTokenProvider(string sourceId) { SourceId = sourceId; }
        public string SourceId { get; }
        public Task<string> GetTokenAsync(CancellationToken ct = default) => Task.FromResult("stub-token");
        public Task<string?> GetAuthenticatedIdentityAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    /// <summary>
    /// Yields one PR (so the orchestrator reports a PR-tied progress event),
    /// then pauses enumeration — after the fast-sync's per-PR upsert has run
    /// but before listing completes — until <see cref="Release"/> is set.
    /// Gives the test a deterministic window in which the row's "syncing"
    /// marker must be observable.
    /// </summary>
    private sealed class BlockingAfterFirstFakeSource : IPrReadSource
    {
        private readonly PrIdentity _id;
        public BlockingAfterFirstFakeSource(string sourceId, PrIdentity id) { SourceId = sourceId; _id = id; }
        public string SourceId { get; }
        public SourceKind Kind => SourceKind.GitHub;
        public SourceCapabilities Capabilities => new(
            SupportsGlobalReviewerInbox: true,
            SupportsThreadResolution: true,
            SupportsBotAuthorClassification: true,
            SupportsReviewRequestTimestamps: true,
            SupportsStableRepoIds: true,
            SupportsForcePushDetection: true);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<RemotePullRequest> ListAssignedFastAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            yield return new RemotePullRequest(
                Identity: _id,
                SourceKind: SourceKind.GitHub,
                SourceId: SourceId,
                DisplayRepo: "owner/repo",
                Number: 1,
                Title: "Sample",
                AuthorLogin: "octocat",
                Url: _id.Url,
                Status: PullRequestStatus.Open,
                LastUpdated: DateTimeOffset.Parse("2026-05-18T10:00:00Z"));

            // Resumes here only after the orchestrator has upserted the PR
            // above and asked for the next item — i.e. after its progress
            // report for this PR has already landed in InboxState.
            Entered.TrySetResult();
            await Release.Task.WaitAsync(ct);
        }

        public Task<PrEnrichmentBundle> EnrichAsync(PrIdentity id, CancellationToken ct) =>
            throw new NotSupportedException("Fast pass only.");
        public async IAsyncEnumerable<RemotePullRequest> ListAuthoredFastAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
        public Task<IReadOnlyList<RemoteCommit>> GetCommitsAsync(PrIdentity id, CancellationToken ct) =>
            throw new NotSupportedException("Fast pass only.");
        public Task<CompareResult> CompareAsync(PrIdentity id, string previousHeadSha, string currentHeadSha, CancellationToken ct) =>
            throw new NotSupportedException("Fast pass only.");
    }

    /// <summary>
    /// Same pause-after-first-PR shape as <see cref="BlockingAfterFirstFakeSource"/>,
    /// but throws once released — proves the active-row marker still clears
    /// on the failure path (the host's <c>finally</c> block must run
    /// regardless of how the task ends).
    /// </summary>
    private sealed class BlockingThenThrowFakeSource : IPrReadSource
    {
        private readonly PrIdentity _id;
        public BlockingThenThrowFakeSource(string sourceId, PrIdentity id) { SourceId = sourceId; _id = id; }
        public string SourceId { get; }
        public SourceKind Kind => SourceKind.GitHub;
        public SourceCapabilities Capabilities => new(
            SupportsGlobalReviewerInbox: true,
            SupportsThreadResolution: true,
            SupportsBotAuthorClassification: true,
            SupportsReviewRequestTimestamps: true,
            SupportsStableRepoIds: true,
            SupportsForcePushDetection: true);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<RemotePullRequest> ListAssignedFastAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            yield return new RemotePullRequest(
                Identity: _id,
                SourceKind: SourceKind.GitHub,
                SourceId: SourceId,
                DisplayRepo: "owner/repo",
                Number: 1,
                Title: "Sample",
                AuthorLogin: "octocat",
                Url: _id.Url,
                Status: PullRequestStatus.Open,
                LastUpdated: DateTimeOffset.Parse("2026-05-18T10:00:00Z"));

            Entered.TrySetResult();
            await Release.Task.WaitAsync(ct);
            throw new InvalidOperationException("simulated upstream failure after first PR");
        }

        public Task<PrEnrichmentBundle> EnrichAsync(PrIdentity id, CancellationToken ct) =>
            throw new NotSupportedException("Fast pass only.");
        public async IAsyncEnumerable<RemotePullRequest> ListAuthoredFastAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
        public Task<IReadOnlyList<RemoteCommit>> GetCommitsAsync(PrIdentity id, CancellationToken ct) =>
            throw new NotSupportedException("Fast pass only.");
        public Task<CompareResult> CompareAsync(PrIdentity id, string previousHeadSha, string currentHeadSha, CancellationToken ct) =>
            throw new NotSupportedException("Fast pass only.");
    }
}
