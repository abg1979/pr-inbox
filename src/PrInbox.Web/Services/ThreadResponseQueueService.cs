using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PrInbox.Core.Reviewing;
using PrInbox.Core.Storage;
using PrInbox.Publishers;

namespace PrInbox.Web.Services;

public sealed record ThreadResponseEdit(string Reply, bool SendReply, bool React, bool Resolve);

public sealed record ThreadResponseReceipt(bool Replied = false, bool Reacted = false, bool Resolved = false);

public sealed record ThreadResponseItem(
    ThreadResponseDraft Draft,
    ThreadResponseEdit Edit,
    ThreadResponseReceipt Receipt,
    IReadOnlyList<ObservedThreadRow> Conversation)
{
    public bool HasPendingActions => (Edit.SendReply && !Receipt.Replied)
        || (Edit.React && !Receipt.Reacted) || (Edit.Resolve && !Receipt.Resolved);
}

public sealed record ThreadResponseQueue(string Revision, IReadOnlyList<ThreadResponseItem> Items);

public sealed record ThreadResponseQueueResult(IReadOnlyList<string> Messages, IReadOnlyList<string> Errors);

public sealed class ThreadResponseQueueService(
    ReviewRunStore runs,
    PullRequestRepository prs,
    ObservedThreadRepository threads,
    IPublisherSelector publishers)
{
    private const string StateFileName = "thread-response-state.json";
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);

    public async Task<ThreadResponseQueue?> LoadAsync(ReviewRun run, CancellationToken ct = default)
    {
        var gate = _gates.GetOrAdd(run.PrUrl, _ => new(1, 1));
        await gate.WaitAsync(ct);
        try { return (await ReadAsync(run, ct))?.Queue; }
        finally { gate.Release(); }
    }

    public async Task SaveEditAsync(ReviewRun run, string revision, string threadId, ThreadResponseEdit edit,
        CancellationToken ct = default)
    {
        var gate = _gates.GetOrAdd(run.PrUrl, _ => new(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            CheckActiveRun(run);
            var loaded = await ReadAsync(run, ct)
                ?? throw new InvalidOperationException("No thread-response drafts available.");
            CheckRevision(loaded.Queue, revision);
            var item = loaded.Queue.Items.Single(i => i.Draft.ThreadId == threadId);
            if ((edit.SendReply && string.IsNullOrWhiteSpace(edit.Reply))
                || (edit.Resolve && !item.Draft.CanResolve)
                || (edit.React && item.Draft.Assessment != "correct"))
                throw new InvalidOperationException("A reply cannot be empty and resolution/reaction must satisfy the verified assessment.");
            var state = EntryFor(loaded.State, item.Draft);
            state.Edit = edit;
            state.DraftHash = Hash(JsonSerializer.Serialize(item.Draft, ThreadResponseDocument.JsonOptions));
            await WriteStateAsync(run, loaded.State, ct);
        }
        finally { gate.Release(); }
    }

    public async Task<ThreadResponseQueueResult> PublishAsync(string prUrl, long runId, string revision,
        IReadOnlyList<string> selectedThreadIds, CancellationToken ct = default)
    {
        var run = runs.Get(prUrl);
        if (run is null || run.RunId != runId)
            return new([], ["The active review changed. Reload and review the new drafts."]);
        var gate = _gates.GetOrAdd(run.PrUrl, _ => new(1, 1));
        await gate.WaitAsync(ct);
        var messages = new List<string>();
        var errors = new List<string>();
        try
        {
            CheckActiveRun(run);
            var loaded = await ReadAsync(run, ct)
                ?? throw new InvalidOperationException("No thread-response drafts available.");
            CheckRevision(loaded.Queue, revision);
            var selected = selectedThreadIds.Distinct(StringComparer.Ordinal)
                .Select(id => loaded.Queue.Items.Single(i => i.Draft.ThreadId == id)).ToList();
            if (selected.Count == 0) throw new InvalidOperationException("No thread actions selected.");
            var pr = await prs.GetAsync(prUrl, ct)
                ?? throw new InvalidOperationException("PR is not in the inbox.");
            var publisher = publishers.SelectFor(prUrl, pr.IdentityUsed);

            // Fail before mutating upstream if publication receipts cannot be persisted.
            await WriteStateAsync(run, loaded.State, ct);
            foreach (var item in selected)
            {
                if (!item.HasPendingActions) continue;
                CheckActiveRun(run);
                var result = await publisher.PostThreadResponseAsync(new(
                    prUrl, run.HeadSha, item.Draft with { Reply = item.Edit.Reply },
                    item.Edit.SendReply && !item.Receipt.Replied,
                    item.Edit.React && !item.Receipt.Reacted,
                    item.Edit.Resolve && !item.Receipt.Resolved), ct);
                var state = EntryFor(loaded.State, item.Draft);
                state.Receipt = new(
                    item.Receipt.Replied || result.Replied,
                    item.Receipt.Reacted || result.Reacted,
                    item.Receipt.Resolved || result.Resolved);
                await WriteStateAsync(run, loaded.State, ct);
                if (result.Resolved)
                    await threads.MarkResolvedByNodeIdsAsync(pr.Identity, [item.Draft.ThreadId], DateTimeOffset.UtcNow, ct);
                if (result.Error is not null)
                {
                    errors.Add($"{item.Draft.ThreadId}: {result.Error}");
                    break;
                }
                messages.Add($"{item.Draft.ThreadId}: approved actions posted as {result.IdentityUsed}.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            errors.Add(ex.Message);
        }
        finally { gate.Release(); }
        return new(messages, errors);
    }

    private async Task<LoadedQueue?> ReadAsync(ReviewRun run, CancellationToken ct)
    {
        var path = Path.Combine(run.RunDirectory, ThreadResponseDocument.FileName);
        if (!File.Exists(path)) return null;
        var json = await File.ReadAllTextAsync(path, ct);
        var doc = ThreadResponseDocument.Parse(json, run.PrUrl, run.HeadSha);
        var statePath = Path.Combine(run.RunDirectory, StateFileName);
        var stateJson = File.Exists(statePath) ? await File.ReadAllTextAsync(statePath, ct) : "";
        var state = stateJson.Length == 0 ? new QueueState()
            : JsonSerializer.Deserialize<QueueState>(stateJson, ThreadResponseDocument.JsonOptions)
                ?? throw new InvalidDataException("Invalid thread-response state file.");
        var pr = await prs.GetAsync(run.PrUrl, ct);
        var conversation = pr is null ? [] : await threads.GetOpenThreadsAsync(pr.Identity, ct);
        var items = doc.Responses.Select(draft =>
        {
            var entry = EntryFor(state, draft);
            var draftHash = Hash(JsonSerializer.Serialize(draft, ThreadResponseDocument.JsonOptions));
            var edit = entry.Edit is not null && entry.DraftHash == draftHash
                ? entry.Edit
                : new ThreadResponseEdit(draft.Reply, true, draft.React, draft.Resolve);
            return new ThreadResponseItem(draft, edit, entry.Receipt,
                conversation.Where(t => t.PlatformThreadNodeId == draft.ThreadId).ToList());
        }).ToList();
        return new(new(Hash(json + "\n" + stateJson), items), state);
    }

    private static Entry EntryFor(QueueState state, ThreadResponseDraft draft)
    {
        var key = $"{draft.ThreadId}:{draft.CommentId}";
        if (!state.Entries.TryGetValue(key, out var entry))
            state.Entries[key] = entry = new();
        return entry;
    }

    private static void CheckRevision(ThreadResponseQueue queue, string revision)
    {
        if (queue.Revision != revision)
            throw new InvalidOperationException("Drafts or edits changed. Reload and review them before approving.");
    }

    private void CheckActiveRun(ReviewRun run)
    {
        if (runs.Get(run.PrUrl)?.RunId != run.RunId)
            throw new InvalidOperationException("The active review changed. Reload and review the new drafts.");
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static async Task WriteStateAsync(ReviewRun run, QueueState state, CancellationToken ct)
    {
        var path = Path.Combine(run.RunDirectory, StateFileName);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(state, ThreadResponseDocument.JsonOptions), ct);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private sealed record LoadedQueue(ThreadResponseQueue Queue, QueueState State);

    private sealed class QueueState
    {
        public Dictionary<string, Entry> Entries { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class Entry
    {
        public string? DraftHash { get; set; }
        public ThreadResponseEdit? Edit { get; set; }
        public ThreadResponseReceipt Receipt { get; set; } = new();
    }
}
