using System.Text.Json;
using PrInbox.Core.Models;
using PrInbox.Core.Reviewing;
using PrInbox.Core.Storage;
using PrInbox.Publishers;
using PrInbox.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PrInbox.Web.Components.Shared;
using System.Text.RegularExpressions;

namespace PrInbox.Tests.Reviewing;

public class ThreadResponseQueueTests : IAsyncLifetime
{
    private const string Url = "https://github.com/owner/repo/pull/42";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"pr-inbox-queue-{Guid.NewGuid():N}");
    private readonly ReviewRunStore _runs = new();
    private readonly Publisher _publisher = new();
    private Microsoft.Data.Sqlite.SqliteConnection _keepAlive = null!;
    private PullRequestRepository _prs = null!;
    private ObservedThreadRepository _threads = null!;
    private ReviewRun _run = null!;

    public async Task InitializeAsync()
    {
        var db = new PrInboxDb(PrInboxDb.InMemoryConnectionString(Guid.NewGuid().ToString("N")));
        _keepAlive = await db.OpenAsync();
        await new MigrationRunner().MigrateAsync(db.ConnectionString);
        _prs = new(db);
        _threads = new(db);
        await _prs.UpsertAsync(new PullRequestRow(
            new(Url, "gh:42"), SourceKind.GitHub, "gh.com", "owner/repo", 42, "Test", "author", Url,
            PullRequestStatus.Open, TrackingReason.Assigned, "bound-reviewer", DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, EnrichState.Enriched, null, null, null), CancellationToken.None);
        Directory.CreateDirectory(_directory);
        _run = new(1, Url, _directory, "abc", DateTimeOffset.UtcNow, null, null, []);
        _runs.StartedRun(_run);
        await WriteDraftsAsync();
    }

    public async Task DisposeAsync()
    {
        await _keepAlive.DisposeAsync();
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task ApprovalUiStartsUnselectedWithEditableDraftAndDisabledPostButton()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Service());
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<ThreadResponses>(
                ParameterView.FromDictionary(new Dictionary<string, object?> { ["Run"] = _run }));
            return component.ToHtmlString();
        });
        Assert.Contains("Proposed reply.", html);
        Assert.Contains("file.cs:10 at abc", html);
        Assert.Contains("#discussion_r123", html);
        Assert.DoesNotContain("checked", Regex.Match(html, "<input[^>]*>").Value);
        Assert.Matches("<button[^>]*disabled[^>]*>\\s*Post selected actions \\(0\\)", html);
        Assert.Equal(0, _publisher.Calls);
    }

    [Fact]
    public async Task LoadingAndEditingNeverPublish_AndEditsSurviveRestart()
    {
        var service = Service();
        var queue = (await service.LoadAsync(_run))!;
        await service.SaveEditAsync(_run, queue.Revision, "PRRT_one", new("Human edit", true, false, false));
        var reloaded = (await Service().LoadAsync(_run))!;
        Assert.Equal("Human edit", reloaded.Items[0].Edit.Reply);
        Assert.False(reloaded.Items[0].Edit.React);
        Assert.False(reloaded.Items[0].Edit.Resolve);
        Assert.Equal(0, _publisher.Calls);
    }

    [Fact]
    public async Task AgentRewritePreservesEditsOnlyForUnchangedDrafts()
    {
        var service = Service();
        var queue = (await service.LoadAsync(_run))!;
        await service.SaveEditAsync(_run, queue.Revision, "PRRT_one", new("Human edit", true, false, false));
        await WriteDraftsAsync();
        Assert.Equal("Human edit", (await service.LoadAsync(_run))!.Items[0].Edit.Reply);
        await WriteDraftsAsync("New agent reply");
        Assert.Equal("New agent reply", (await service.LoadAsync(_run))!.Items[0].Edit.Reply);
    }

    [Fact]
    public async Task PublishingUsesBoundIdentityAndOnlySelectedSavedActions()
    {
        var service = Service();
        var queue = (await service.LoadAsync(_run))!;
        await service.SaveEditAsync(_run, queue.Revision, "PRRT_one", new("Human edit", true, false, false));
        queue = (await service.LoadAsync(_run))!;
        var result = await service.PublishAsync(Url, 1, queue.Revision, ["PRRT_one"]);
        Assert.Empty(result.Errors);
        Assert.Equal("bound-reviewer", _publisher.Identity);
        Assert.Equal("Human edit", _publisher.Last!.Draft.Reply);
        Assert.True(_publisher.Last.Reply);
        Assert.False(_publisher.Last.React);
        Assert.False(_publisher.Last.Resolve);
        Assert.Equal(1, _publisher.Calls);
        var afterRestart = (await Service().LoadAsync(_run))!;
        Assert.True(afterRestart.Items[0].Receipt.Replied);
        Assert.False(afterRestart.Items[0].HasPendingActions);
        Assert.True(afterRestart.Items[1].HasPendingActions);
    }

    [Fact]
    public async Task ChangedDraftsOrReplacedRunInvalidateApproval()
    {
        var service = Service();
        var queue = (await service.LoadAsync(_run))!;
        await WriteDraftsAsync("New evidence and proposal.");
        var result = await service.PublishAsync(Url, 1, queue.Revision, ["PRRT_one"]);
        Assert.NotEmpty(result.Errors);
        Assert.Equal(0, _publisher.Calls);
        _runs.StartedRun(_run with { RunId = 2 });
        result = await service.PublishAsync(Url, 1, queue.Revision, ["PRRT_one"]);
        Assert.NotEmpty(result.Errors);
        Assert.Equal(0, _publisher.Calls);
    }

    [Fact]
    public async Task PartialFailurePersistsCompletedActionsAndRetrySkipsThem()
    {
        var service = Service();
        var queue = (await service.LoadAsync(_run))!;
        _publisher.FailReaction = true;
        var result = await service.PublishAsync(Url, 1, queue.Revision, ["PRRT_one"]);
        Assert.NotEmpty(result.Errors);
        queue = (await Service().LoadAsync(_run))!;
        Assert.True(queue.Items[0].Receipt.Replied);
        Assert.False(queue.Items[0].Receipt.Reacted);
        _publisher.FailReaction = false;
        result = await service.PublishAsync(Url, 1, queue.Revision, ["PRRT_one"]);
        Assert.Empty(result.Errors);
        Assert.False(_publisher.Last!.Reply);
        Assert.True(_publisher.Last.React);
        Assert.True(_publisher.Last.Resolve);
        Assert.False((await service.LoadAsync(_run))!.Items[0].HasPendingActions);
    }

    private ThreadResponseQueueService Service() => new(_runs, _prs, _threads, _publisher);

    private Task WriteDraftsAsync(string reply = "Proposed reply.")
        => File.WriteAllTextAsync(Path.Combine(_directory, ThreadResponseDocument.FileName),
            JsonSerializer.Serialize(new ThreadResponseDocument
            {
                SchemaVersion = 1, PrUrl = Url, HeadSha = "abc",
                Responses = new[] { ("PRRT_one", 123L), ("PRRT_two", 124L) }
                    .Select(pair => new ThreadResponseDraft
                    {
                        ThreadId = pair.Item1, CommentId = pair.Item2, Assessment = "correct", SettleState = "fixed",
                        Evidence = "file.cs:10 at abc", Reply = reply, Verified = true, React = true, Resolve = true,
                    }).ToList(),
            }, ThreadResponseDocument.JsonOptions));

    private sealed class Publisher : IPrReviewPublisher, IPublisherSelector
    {
        public string Kind => "fake";
        public string? Identity { get; private set; }
        public int Calls { get; private set; }
        public ThreadResponseRequest? Last { get; private set; }
        public bool FailReaction { get; set; }
        public IPrReviewPublisher Select(string prUrl) => this;
        public IPrReviewPublisher SelectFor(string prUrl, string identityUsed) { Identity = identityUsed; return this; }
        public string? IdentityForLogging(string prUrl) => Identity;
        public Task<PublishResult> PublishAsync(PublishRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<ThreadResolveResult> ResolveThreadsAsync(ThreadResolveRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<ThreadResponseResult> PostThreadResponseAsync(ThreadResponseRequest request, CancellationToken ct)
        {
            Calls++;
            Last = request;
            return Task.FromResult(new ThreadResponseResult(request.Reply,
                !FailReaction && request.React, !FailReaction && request.Resolve, Identity!,
                FailReaction ? "Reaction failed." : null));
        }
    }
}
