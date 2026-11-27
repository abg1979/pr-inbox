using System.Text.Json;
using PrInbox.Core.Reviewing;

namespace PrInbox.Tests.Reviewing;

public class ThreadResponseDocumentTests
{
    private const string Url = "https://github.com/owner/repo/pull/42";

    [Theory]
    [InlineData("correct", "open", true, "n/a", false)]
    [InlineData("correct", "fixed", true, "n/a", true)]
    [InlineData("correct", "fixed", false, "n/a", false)]
    [InlineData("uncertain", "fixed", true, "n/a", false)]
    [InlineData("incorrect", "open", true, "n/a", true)]
    [InlineData("correct", "rejected_by_author", true, "agree", true)]
    [InlineData("correct", "rejected_by_author", true, "disagree", false)]
    public void ResolutionRequiresVerifiedSettledEvidence(
        string assessment, string settleState, bool verified, string stance, bool allowed)
    {
        var draft = Draft() with
        {
            Assessment = assessment, SettleState = settleState, Verified = verified, OurStance = stance, Resolve = true,
        };
        var json = Json(draft);
        if (allowed)
            Assert.Single(ThreadResponseDocument.Parse(json, Url, "abc").Responses);
        else
            Assert.Throws<InvalidDataException>(() => ThreadResponseDocument.Parse(json, Url, "abc"));
    }

    [Fact]
    public void RejectsWrongPrHeadDuplicateIdsAndNullEntries()
    {
        var json = Json(Draft());
        Assert.Throws<InvalidDataException>(() => ThreadResponseDocument.Parse(json, Url + "0", "abc"));
        Assert.Throws<InvalidDataException>(() => ThreadResponseDocument.Parse(json, Url, "new-head"));
        Assert.Throws<InvalidDataException>(() => ThreadResponseDocument.Parse(Json(Draft(), Draft()), Url, "abc"));
        Assert.Throws<InvalidDataException>(() => ThreadResponseDocument.Parse(Json([null!]), Url, "abc"));
    }

    private static ThreadResponseDraft Draft() => new()
    {
        ThreadId = "PRRT_one", CommentId = 123, Assessment = "correct", SettleState = "open",
        Evidence = "src/file.cs:10 at abc", Reply = "Confirmed, please fix this.", Verified = true,
    };

    private static string Json(params ThreadResponseDraft[] drafts) => JsonSerializer.Serialize(
        new ThreadResponseDocument { SchemaVersion = 1, PrUrl = Url, HeadSha = "abc", Responses = drafts.ToList() },
        ThreadResponseDocument.JsonOptions);
}
