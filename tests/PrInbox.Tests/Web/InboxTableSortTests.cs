using System.Text.Json;
using FluentAssertions;
using PrInbox.Core.Findings;
using PrInbox.Core.Models;
using PrInbox.Core.Storage;
using PrInbox.Web.Services;

namespace PrInbox.Tests.Web;

public class InboxTableSortTests
{
    private static readonly DateTimeOffset Earlier = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
    private static readonly DateTimeOffset Later = Earlier.AddDays(1);
    private static readonly IReadOnlyDictionary<string, ReviewRun> NoRuns = new Dictionary<string, ReviewRun>();

    private static InboxRow Row(string url) => new(
        Url: url,
        DisplayRepo: "o/r",
        Number: 1,
        Title: "Title",
        AuthorLogin: "author",
        SourceId: "gh.com",
        SourceKind: SourceKind.GitHub,
        IdentityUsed: "id",
        Status: PullRequestStatus.Open,
        EnrichState: EnrichState.Enriched,
        LastSyncedAt: Earlier,
        OpenThreadCount: 0,
        UnresolvedBotCount: 0,
        DriftKind: DriftKind.Unknown,
        DriftCount: 0,
        LastReviewedHeadSha: null,
        CurrentHeadSha: null,
        UpstreamCreatedAt: Earlier,
        ApprovalCount: 0);

    private static ReviewRun Run(string url, params FindingSeverity[] severities) => new(
        1, url, "run", "sha", Earlier, Earlier,
        new FindingsDocument
        {
            Findings = severities.Select(severity => new Finding { Severity = severity }).ToArray(),
        },
        Array.Empty<string>());

    [Theory]
    [InlineData(InboxSortColumn.Source)]
    [InlineData(InboxSortColumn.Repo)]
    [InlineData(InboxSortColumn.Title)]
    [InlineData(InboxSortColumn.Author)]
    [InlineData(InboxSortColumn.Threads)]
    [InlineData(InboxSortColumn.State)]
    [InlineData(InboxSortColumn.Approvals)]
    [InlineData(InboxSortColumn.Age)]
    [InlineData(InboxSortColumn.LastSync)]
    [InlineData(InboxSortColumn.Review)]
    public void Every_Data_Column_Sorts_In_Both_Directions(InboxSortColumn column)
    {
        var first = Row("first") with
        {
            SourceId = "Alpha", DisplayRepo = "Alpha/repo", Title = "Alpha", AuthorLogin = "Alpha",
            Status = PullRequestStatus.Closed, OpenThreadCount = 2, ApprovalCount = 2,
        };
        var second = Row("second") with
        {
            SourceId = "beta", DisplayRepo = "beta/repo", Title = "beta", AuthorLogin = "beta",
            Status = PullRequestStatus.Open, OpenThreadCount = 10, ApprovalCount = 10,
            UpstreamCreatedAt = Later, LastSyncedAt = Later,
        };
        var rows = new[] { second, first };
        var runs = new Dictionary<string, ReviewRun>
        {
            [first.Url] = Run(first.Url, FindingSeverity.High, FindingSeverity.High),
            [second.Url] = Run(second.Url, FindingSeverity.Critical),
        };

        new InboxTableSort(column).Order(rows, row => row.SourceId, runs)
            .Select(row => row.Url).Should().Equal("first", "second");
        new InboxTableSort(column, true).Order(rows, row => row.SourceId, runs)
            .Select(row => row.Url).Should().Equal("second", "first");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Repo_Sort_Uses_Numeric_Pr_Number_Within_Repo(bool descending)
    {
        var rows = new[]
        {
            Row("ten") with { Number = 10 },
            Row("two") with { Number = 2 },
            Row("other") with { DisplayRepo = "z/r", Number = 1 },
        };

        var ordered = new InboxTableSort(InboxSortColumn.Repo, descending)
            .Order(rows, row => row.SourceId, NoRuns);

        ordered.Select(row => row.Url).Should().Equal(
            descending ? new[] { "other", "ten", "two" } : new[] { "two", "ten", "other" });
    }

    [Theory]
    [InlineData(InboxSortColumn.Title, false)]
    [InlineData(InboxSortColumn.Title, true)]
    [InlineData(InboxSortColumn.Author, false)]
    [InlineData(InboxSortColumn.Author, true)]
    [InlineData(InboxSortColumn.Approvals, false)]
    [InlineData(InboxSortColumn.Approvals, true)]
    [InlineData(InboxSortColumn.Age, false)]
    [InlineData(InboxSortColumn.Age, true)]
    [InlineData(InboxSortColumn.Review, false)]
    [InlineData(InboxSortColumn.Review, true)]
    public void Missing_Values_Stay_Last_In_Both_Directions(InboxSortColumn column, bool descending)
    {
        var missing = Row("missing") with
        {
            Title = null, AuthorLogin = null, ApprovalCount = null, UpstreamCreatedAt = null,
        };
        var rows = new[] { missing, Row("known") };
        var runs = new Dictionary<string, ReviewRun> { ["known"] = Run("known") };

        new InboxTableSort(column, descending).Order(rows, row => row.SourceId, runs)
            .Select(row => row.Url).Should().Equal("known", "missing");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Equal_Keys_Preserve_Incoming_Order(bool descending)
    {
        var rows = new[] { Row("second"), Row("first") };

        foreach (var column in Enum.GetValues<InboxSortColumn>())
        {
            new InboxTableSort(column, descending).Order(rows, row => row.SourceId, NoRuns)
                .Select(row => row.Url).Should().Equal("second", "first");
        }
    }

    [Fact]
    public void Source_Sort_Uses_The_Displayed_Label_Not_The_Source_Id()
    {
        var rows = new[] { Row("public"), Row("ado") };

        new InboxTableSort(InboxSortColumn.Source)
            .Order(rows, row => row.Url == "public" ? "public" : "ADO", NoRuns)
            .Select(row => row.Url).Should().Equal("ado", "public");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Review_Sort_Handles_In_Progress_Clean_And_Unreviewed_Rows(bool descending)
    {
        var rows = new[] { Row("unreviewed"), Row("findings"), Row("clean"), Row("running") };
        var runs = new Dictionary<string, ReviewRun>
        {
            ["findings"] = Run("findings", FindingSeverity.Low),
            ["clean"] = Run("clean"),
            ["running"] = Run("running") with { Findings = null, FindingsAtUtc = null },
        };

        new InboxTableSort(InboxSortColumn.Review, descending).Order(rows, row => row.SourceId, runs)
            .Select(row => row.Url).Should().Equal(descending
                ? new[] { "findings", "clean", "running", "unreviewed" }
                : new[] { "running", "clean", "findings", "unreviewed" });
    }

    [Fact]
    public void Header_Click_Cycles_Ascending_Descending_And_Default()
    {
        var ascending = new InboxTableSort().Next(InboxSortColumn.Title);
        ascending.Should().Be(new InboxTableSort(InboxSortColumn.Title));
        var descending = ascending.Next(InboxSortColumn.Title);
        descending.Should().Be(new InboxTableSort(InboxSortColumn.Title, true));
        descending.Next(InboxSortColumn.Title).Should().Be(new InboxTableSort());
        descending.Next(InboxSortColumn.Approvals).Should().Be(new InboxTableSort(InboxSortColumn.Approvals));
    }

    [Fact]
    public void Default_Order_Is_Unchanged()
    {
        var rows = new[] { Row("second"), Row("first") };

        new InboxTableSort().Order(rows, row => row.SourceId, NoRuns).Should().BeSameAs(rows);
    }

    [Theory]
    [InlineData(AgeSortMode.None, InboxSortColumn.None, false)]
    [InlineData(AgeSortMode.OldestFirst, InboxSortColumn.Age, false)]
    [InlineData(AgeSortMode.NewestFirst, InboxSortColumn.Age, true)]
    public void Existing_Age_Preferences_Are_Preserved(AgeSortMode legacy, InboxSortColumn column, bool descending)
    {
        InboxTableSort.FromLegacyAge(legacy).Should().Be(new InboxTableSort(column, descending));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Sort_Preference_RoundTrips(bool descending)
    {
        foreach (var column in Enum.GetValues<InboxSortColumn>())
        {
            var sort = new InboxTableSort(column, descending);
            JsonSerializer.Deserialize<InboxTableSort>(JsonSerializer.Serialize(sort)).Should().Be(sort);
        }
    }
}
