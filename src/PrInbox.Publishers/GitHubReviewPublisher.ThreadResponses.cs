using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace PrInbox.Publishers;

public sealed partial class GitHubReviewPublisher
{
    public async Task<ThreadResponseResult> PostThreadResponseAsync(ThreadResponseRequest request, CancellationToken ct)
    {
        var replied = false;
        var reacted = false;
        var resolved = false;
        try
        {
            var target = PullRequestUrlParser.Parse(request.PrUrl);
            if (target.Kind == PrPlatform.AzureDevOps
                || !target.Host.Equals(_host, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The selected publisher does not match the PR host.");
            var draft = request.Draft;
            if (draft.CommentId <= 0 || string.IsNullOrWhiteSpace(draft.ThreadId)
                || string.IsNullOrWhiteSpace(request.HeadSha)
                || (request.Reply && string.IsNullOrWhiteSpace(draft.Reply))
                || (request.Resolve && !draft.CanResolve)
                || (request.React && draft.Assessment != "correct"))
                throw new InvalidOperationException("Invalid thread response or unverified resolution.");

            var token = await _tokens.GetTokenAsync(ct);
            var head = await TryGetCurrentHeadAsync(token, target, ct);
            if (head is null)
                throw new InvalidOperationException("Could not verify the current PR HEAD. Nothing was posted.");
            if (!head.Equals(request.HeadSha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Stale drafts: PR HEAD changed. Run a new review before posting thread actions.");

            var marker = $"<!-- pr-inbox-thread-response:{request.HeadSha}:{draft.CommentId} -->";
            var alreadyReplied = false;
            string? cursor = null;
            var firstPage = true;
            do
            {
                using var page = await SendThreadJsonAsync(token, HttpMethod.Post, GraphqlBase(target), new
                {
                    query = """
                        query($id: ID!, $cursor: String) {
                          viewer { login }
                          node(id: $id) {
                            ... on PullRequestReviewThread {
                              isResolved isOutdated
                              pullRequest { number repository { nameWithOwner } }
                              comments(first: 100, after: $cursor) {
                                nodes { databaseId body author { login } }
                                pageInfo { hasNextPage endCursor }
                              }
                            }
                          }
                        }
                        """,
                    variables = new { id = draft.ThreadId, cursor },
                }, ct);
                var root = page.RootElement;
                ThrowGraphqlErrors(root);
                var data = root.GetProperty("data");
                var thread = data.GetProperty("node");
                var pr = thread.GetProperty("pullRequest");
                if (pr.GetProperty("number").GetInt32() != target.Number
                    || !string.Equals(pr.GetProperty("repository").GetProperty("nameWithOwner").GetString(),
                        $"{target.Owner}/{target.Repo}", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Thread does not belong to this PR.");
                if (thread.GetProperty("isOutdated").GetBoolean())
                    throw new InvalidOperationException("Thread is outdated. Regenerate and verify the draft.");
                var comments = thread.GetProperty("comments");
                var nodes = comments.GetProperty("nodes");
                if (firstPage && (nodes.GetArrayLength() == 0
                    || nodes[0].GetProperty("databaseId").GetInt64() != draft.CommentId))
                    throw new InvalidOperationException("Reply target is not the root comment of this thread.");
                if (thread.GetProperty("isResolved").GetBoolean())
                    throw new InvalidOperationException("Thread is already resolved. Refresh the review.");
                var viewer = data.GetProperty("viewer").GetProperty("login").GetString();
                foreach (var comment in nodes.EnumerateArray())
                {
                    var author = comment.GetProperty("author");
                    if (author.ValueKind == JsonValueKind.Object
                        && string.Equals(author.GetProperty("login").GetString(), viewer, StringComparison.OrdinalIgnoreCase)
                        && (comment.GetProperty("body").GetString()?.Contains(marker, StringComparison.Ordinal) ?? false))
                        alreadyReplied = true;
                }
                var info = comments.GetProperty("pageInfo");
                cursor = info.GetProperty("hasNextPage").GetBoolean()
                    ? info.GetProperty("endCursor").GetString()
                    : null;
                if (info.GetProperty("hasNextPage").GetBoolean() && string.IsNullOrEmpty(cursor))
                    throw new InvalidOperationException("Incomplete thread conversation returned by GitHub.");
                firstPage = false;
            } while (cursor is not null);

            var api = $"{ApiBase(target)}/repos/{target.Owner}/{target.Repo}";
            if (request.Reply)
            {
                if (!alreadyReplied)
                {
                    using var response = await SendThreadJsonAsync(token, HttpMethod.Post,
                        $"{api}/pulls/{target.Number}/comments/{draft.CommentId}/replies",
                        new { body = $"{draft.Reply.Trim()}\n\n{marker}" }, ct);
                }
                replied = true;
            }
            if (request.React)
            {
                using var response = await SendThreadJsonAsync(token, HttpMethod.Post,
                    $"{api}/pulls/comments/{draft.CommentId}/reactions", new { content = "+1" }, ct);
                reacted = true;
            }
            if (request.Resolve)
            {
                var outcome = await ResolveOneAsync(token, target, draft.ThreadId, ct);
                if (outcome.Status == ResolveStatus.Failed)
                    throw new InvalidOperationException(outcome.Error ?? "Could not resolve thread.");
                resolved = true;
            }
            return new(replied, reacted, resolved, _identityUsed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Preserve successful steps so a retry never repeats them locally.
            return new(replied, reacted, resolved, _identityUsed, ex.Message);
        }
    }

    private async Task<JsonDocument> SendThreadJsonAsync(
        string token, HttpMethod method, string url, object payload, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("pr-inbox", "0.2"));
        request.Content = JsonContent.Create(payload);
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"GitHub returned {(int)response.StatusCode} for a thread action. Check permissions and refresh before retrying.");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    private static void ThrowGraphqlErrors(JsonElement root)
    {
        if (root.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0)
            throw new InvalidOperationException($"Could not verify the thread: {errors}");
    }
}
