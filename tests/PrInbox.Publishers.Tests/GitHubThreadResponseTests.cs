using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PrInbox.Core.Reviewing;
using Xunit;

namespace PrInbox.Publishers.Tests;

public class GitHubThreadResponseTests
{
    [Theory]
    [InlineData("changed")]
    [InlineData("unavailable")]
    [InlineData("wrong-pr")]
    [InlineData("wrong-root")]
    [InlineData("outdated")]
    [InlineData("resolved")]
    [InlineData("graphql-error")]
    public async Task RefusesMutationsWhenHeadOrThreadCannotBeVerified(string failure)
    {
        var handler = new RecordingHandler(req => req.Method == HttpMethod.Get
            ? failure == "unavailable" ? new(HttpStatusCode.Forbidden)
                : Json(new { head = new { sha = failure == "changed" ? "new-head" : "abc" } })
            : ThreadPage(failure));
        using var http = new HttpClient(handler);
        var result = await Publisher(http).PostThreadResponseAsync(Request(), CancellationToken.None);
        Assert.NotNull(result.Error);
        Assert.False(result.Replied);
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("/replies")
            || r.RequestUri.AbsolutePath.EndsWith("/reactions"));
    }

    [Fact]
    public async Task PostsOnlyApprovedActionsWithEditedReplyAndIdentity()
    {
        var handler = new RecordingHandler(req =>
        {
            if (req.Method == HttpMethod.Get) return Json(new { head = new { sha = "abc" } });
            if (req.RequestUri!.AbsolutePath == "/graphql") return ThreadPage();
            Assert.Equal("/repos/owner/repo/pulls/42/comments/123/replies", req.RequestUri.AbsolutePath);
            var payload = JsonDocument.Parse(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal("Edited reply.\n\n<!-- pr-inbox-thread-response:abc:123 -->",
                payload.RootElement.GetProperty("body").GetString());
            Assert.Equal("tk", req.Headers.Authorization!.Parameter);
            return Json(new { id = 456 });
        });
        using var http = new HttpClient(handler);
        var result = await Publisher(http).PostThreadResponseAsync(Request(), CancellationToken.None);
        Assert.Null(result.Error);
        Assert.True(result.Replied);
        Assert.False(result.Reacted);
        Assert.False(result.Resolved);
        Assert.Equal("reviewer", result.IdentityUsed);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task PartialFailureReportsSuccessfulReplyAndDoesNotResolve()
    {
        var handler = new RecordingHandler(req =>
        {
            if (req.Method == HttpMethod.Get) return Json(new { head = new { sha = "abc" } });
            if (req.RequestUri!.AbsolutePath == "/graphql") return ThreadPage();
            return req.RequestUri.AbsolutePath.EndsWith("/replies")
                ? Json(new { id = 456 })
                : new(HttpStatusCode.Forbidden);
        });
        using var http = new HttpClient(handler);
        var request = Request() with { React = true, Resolve = true };
        var result = await Publisher(http).PostThreadResponseAsync(request, CancellationToken.None);
        Assert.NotNull(result.Error);
        Assert.True(result.Replied);
        Assert.False(result.Reacted);
        Assert.False(result.Resolved);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Theory]
    [InlineData("reviewer", true)]
    [InlineData("someone-else", false)]
    public async Task RetryFindsOnlyOwnReplyMarkerAcrossPages(string markerAuthor, bool skipsReply)
    {
        var pages = 0;
        var replies = 0;
        var handler = new RecordingHandler(req =>
        {
            if (req.Method == HttpMethod.Get) return Json(new { head = new { sha = "abc" } });
            if (req.RequestUri!.AbsolutePath == "/graphql")
            {
                pages++;
                return pages == 1
                    ? ThreadPage(next: true)
                    : ThreadPage(markerAuthor: markerAuthor, commentId: 456);
            }
            replies++;
            return Json(new { id = 789 });
        });
        using var http = new HttpClient(handler);
        var result = await Publisher(http).PostThreadResponseAsync(Request(), CancellationToken.None);
        Assert.Null(result.Error);
        Assert.True(result.Replied);
        Assert.Equal(2, pages);
        Assert.Equal(skipsReply ? 0 : 1, replies);
    }

    [Fact]
    public async Task ReactionAndResolutionCanBeApprovedWithoutReply()
    {
        var queries = 0;
        var handler = new RecordingHandler(req =>
        {
            if (req.Method == HttpMethod.Get) return Json(new { head = new { sha = "abc" } });
            if (req.RequestUri!.AbsolutePath == "/graphql")
            {
                queries++;
                if (queries == 1) return ThreadPage();
                return Json(new { data = new { resolveReviewThread = new { thread = new { isResolved = true } } } });
            }
            Assert.Equal("/repos/owner/repo/pulls/comments/123/reactions", req.RequestUri.AbsolutePath);
            return Json(new { id = 789 });
        });
        using var http = new HttpClient(handler);
        var result = await Publisher(http).PostThreadResponseAsync(
            Request() with { Reply = false, React = true, Resolve = true }, CancellationToken.None);
        Assert.Null(result.Error);
        Assert.False(result.Replied);
        Assert.True(result.Reacted);
        Assert.True(result.Resolved);
    }

    private static GitHubReviewPublisher Publisher(HttpClient http) => new(
        new FakeTokenProvider(_ => "tk"), http, false, "github.com", "reviewer",
        NullLogger<GitHubReviewPublisher>.Instance);

    private static ThreadResponseRequest Request() => new(
        "https://github.com/owner/repo/pull/42", "abc",
        new ThreadResponseDraft
        {
            ThreadId = "PRRT_one", CommentId = 123, Assessment = "correct", SettleState = "fixed",
            Verified = true, Evidence = "src/file.cs:10 at abc, regression check passed.", Reply = "Edited reply.",
        }, true, false, false);

    private static HttpResponseMessage ThreadPage(string failure = "", bool next = false,
        string? markerAuthor = null, long commentId = 123)
    {
        if (failure == "graphql-error") return Json(new { errors = new[] { new { message = "Access denied" } } });
        return Json(new
        {
            data = new
            {
                viewer = new { login = "reviewer" },
                node = new
                {
                    isResolved = failure == "resolved",
                    isOutdated = failure == "outdated",
                    pullRequest = new
                    {
                        number = failure == "wrong-pr" ? 43 : 42,
                        repository = new { nameWithOwner = "owner/repo" },
                    },
                    comments = new
                    {
                        nodes = new[] { new
                        {
                            databaseId = failure == "wrong-root" ? 999 : commentId,
                            body = markerAuthor is null ? "Original" : "<!-- pr-inbox-thread-response:abc:123 -->",
                            author = new { login = markerAuthor ?? "Copilot" },
                        } },
                        pageInfo = new { hasNextPage = next, endCursor = next ? "next-page" : (string?)null },
                    },
                },
            },
        });
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value), System.Text.Encoding.UTF8, "application/json"),
    };
}
