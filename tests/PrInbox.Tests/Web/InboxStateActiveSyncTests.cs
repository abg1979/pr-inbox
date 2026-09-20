using PrInbox.Web.Services;

namespace PrInbox.Tests.Web;

/// <summary>
/// Locks the row-level sync activity contract on <see cref="InboxState"/>:
/// each concurrently running sync task (fast-sync fan-out, visible/hidden
/// enrich passes) reports its own progress under a stable
/// <c>sourceKey</c>, and <see cref="InboxState.GetActiveRowActivity"/>
/// looks up a PR's live activity by URL without one source's update ever
/// clobbering another's. This is the contract the Inbox/My PRs "syncing"
/// row indicator depends on.
/// </summary>
public class InboxStateActiveSyncTests
{
    private const string PrUrl = "https://github.com/owner/repo/pull/1";
    private const string OtherPrUrl = "https://github.com/owner/repo/pull/2";

    [Fact]
    public void GetActiveRowActivity_Returns_Null_When_Idle()
    {
        var state = new InboxState();
        state.GetActiveRowActivity(PrUrl).Should().BeNull();
    }

    [Fact]
    public void NoteSyncActivity_With_PrUrl_Surfaces_Via_GetActiveRowActivity()
    {
        var state = new InboxState();
        state.NoteSyncActivity("gh.com:emu::alice", PrUrl, "Syncing #1 owner/repo · gh.com:emu");

        state.GetActiveRowActivity(PrUrl).Should().Be("Syncing #1 owner/repo · gh.com:emu");
        state.GetActiveRowActivity(OtherPrUrl).Should().BeNull();
    }

    [Fact]
    public void NoteSyncActivity_With_Null_PrUrl_Does_Not_Surface_As_Row_Activity()
    {
        // Phase-level events ("Fetching inbox") must never highlight an
        // arbitrary row — only PR-tied events may.
        var state = new InboxState();
        state.NoteSyncActivity("gh.com:emu::alice", prUrl: null, label: "Fetching inbox");

        state.GetActiveRowActivity(PrUrl).Should().BeNull();
    }

    [Fact]
    public void NoteSyncActivity_With_Null_Label_Clears_The_Key()
    {
        var state = new InboxState();
        state.NoteSyncActivity("gh.com:emu::alice", PrUrl, "Syncing #1 owner/repo · gh.com:emu");
        state.GetActiveRowActivity(PrUrl).Should().NotBeNull();

        state.NoteSyncActivity("gh.com:emu::alice", null, null);

        state.GetActiveRowActivity(PrUrl).Should().BeNull();
    }

    [Fact]
    public void Two_Sources_Active_At_Once_Both_Surface_Independently()
    {
        // Simulates the parallel fast-sync fan-out: two runtimes each own
        // a distinct sourceKey and can be mid-PR simultaneously.
        var state = new InboxState();
        state.NoteSyncActivity("gh.com:emu::alice", PrUrl, "Syncing #1 owner/repo · gh.com:emu");
        state.NoteSyncActivity("gh.com:public::bob", OtherPrUrl, "Syncing #2 owner/repo · gh.com:public");

        state.GetActiveRowActivity(PrUrl).Should().Be("Syncing #1 owner/repo · gh.com:emu");
        state.GetActiveRowActivity(OtherPrUrl).Should().Be("Syncing #2 owner/repo · gh.com:public");

        // Clearing one source's key must not affect the other's.
        state.NoteSyncActivity("gh.com:emu::alice", null, null);

        state.GetActiveRowActivity(PrUrl).Should().BeNull();
        state.GetActiveRowActivity(OtherPrUrl).Should().Be("Syncing #2 owner/repo · gh.com:public");
    }

    [Fact]
    public void GetActiveRowActivity_Is_Case_Insensitive_On_Url()
    {
        var state = new InboxState();
        state.NoteSyncActivity("gh.com:emu::alice", PrUrl, "Syncing #1 owner/repo · gh.com:emu");

        state.GetActiveRowActivity(PrUrl.ToUpperInvariant()).Should().NotBeNull();
    }

    [Fact]
    public void Re_Reporting_The_Same_Key_With_A_Different_Pr_Replaces_The_Previous_Row()
    {
        // A single fast-sync task moves from PR to PR sequentially — the
        // previous PR must stop showing "syncing" once the task advances.
        var state = new InboxState();
        state.NoteSyncActivity("gh.com:emu::alice", PrUrl, "Syncing #1 owner/repo · gh.com:emu");
        state.GetActiveRowActivity(PrUrl).Should().NotBeNull();

        state.NoteSyncActivity("gh.com:emu::alice", OtherPrUrl, "Syncing #2 owner/repo · gh.com:emu");

        state.GetActiveRowActivity(PrUrl).Should().BeNull();
        state.GetActiveRowActivity(OtherPrUrl).Should().NotBeNull();
    }

    [Fact]
    public void NoteSyncActivity_Raises_Changed()
    {
        var state = new InboxState();
        var raised = 0;
        state.Changed += () => raised++;

        state.NoteSyncActivity("gh.com:emu::alice", PrUrl, "Syncing #1 owner/repo · gh.com:emu");

        raised.Should().Be(1);
    }
}
