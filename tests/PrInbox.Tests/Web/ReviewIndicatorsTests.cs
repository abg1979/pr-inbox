using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PrInbox.Core.Models;
using PrInbox.Core.Storage;
using PrInbox.Web.Components.Shared;
using PrInbox.Web.Services;

namespace PrInbox.Tests.Web;

public class ReviewIndicatorsTests
{
    private static InboxRow Row(string? viewed, string? current, OwnApproval? approval = null)
    {
        var pr = new PullRequestRow(
            new("https://github.com/o/r/pull/1", "gh:1"), SourceKind.GitHub, "gh.com", "o/r", 1,
            "Test", "author", "https://github.com/o/r/pull/1", PullRequestStatus.Open,
            TrackingReason.Assigned, "alice", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            EnrichState.Enriched, viewed, null, null);
        return InboxRow.FromRow(pr, 0, 0, new(DriftKind.Unknown, 0, null, current),
            ownApproval: approval);
    }

    [Fact]
    public async Task No_Review_Launch_Or_Own_Approval_Shows_No_Indicators()
    {
        (await RenderAsync(Row(null, "head") with { ApprovalCount = 3 })).Should().BeEmpty();
    }

    [Theory]
    [InlineData("head", "Viewed latest commit")]
    [InlineData("new-head", "Viewed older commit")]
    [InlineData(null, "Viewed")]
    public async Task Viewed_Uses_Persisted_Review_Launch_Commit(string? current, string expected)
    {
        var html = await RenderAsync(Row("head", current));
        html.Should().Contain(expected).And.NotContain("You approved");
    }

    [Theory]
    [InlineData("head", "head", "You approved latest commit")]
    [InlineData("old-head", "head", "You approved an older commit")]
    [InlineData(null, "head", "You approved (commit unverified)")]
    [InlineData("head", null, "You approved (commit unverified)")]
    public async Task Own_Approval_Requires_Exact_Known_Head(string? approved, string? current, string expected)
    {
        var html = await RenderAsync(Row(null, current, new("alice", approved)));
        html.Should().Contain(expected).And.Contain("alice").And.NotContain("Viewed");
        if (expected != "You approved latest commit")
            html.Should().NotContain("You approved latest commit");
    }

    private static async Task<string> RenderAsync(InboxRow row)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<ReviewIndicators>(
                ParameterView.FromDictionary(new Dictionary<string, object?> { ["Row"] = row }));
            return component.ToHtmlString();
        });
    }
}
