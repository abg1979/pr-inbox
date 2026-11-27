using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrInbox.Core.Reviewing;

public sealed record ThreadResponseDraft
{
    public required string ThreadId { get; init; }
    public required long CommentId { get; init; }
    public required string Assessment { get; init; }
    public required string SettleState { get; init; }
    public string OurStance { get; init; } = "n/a";
    public bool Verified { get; init; }
    public required string Evidence { get; init; }
    public required string Reply { get; init; }
    public bool React { get; init; }
    public bool Resolve { get; init; }

    [JsonIgnore]
    public bool CanResolve => Verified && !string.IsNullOrWhiteSpace(Evidence)
        && Assessment is "correct" or "incorrect"
        && (Assessment == "incorrect" || SettleState == "fixed"
            || (SettleState == "rejected_by_author" && OurStance == "agree"));
}

public sealed record ThreadResponseDocument
{
    public const string FileName = "thread-responses.json";
    public int SchemaVersion { get; init; }
    public required string PrUrl { get; init; }
    public required string HeadSha { get; init; }
    public required List<ThreadResponseDraft> Responses { get; init; }

    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static ThreadResponseDocument Parse(string json, string prUrl, string headSha)
    {
        var doc = JsonSerializer.Deserialize<ThreadResponseDocument>(json, JsonOptions)
            ?? throw new InvalidDataException("Empty thread-response document.");
        if (doc.SchemaVersion != 1
            || !string.Equals(doc.PrUrl, prUrl, StringComparison.Ordinal)
            || !string.Equals(doc.HeadSha, headSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Thread-response schema, PR URL, or HEAD does not match this review run. Regenerate the drafts.");
        if (doc.Responses is null
            || doc.Responses.Any(r => r is null
                || string.IsNullOrWhiteSpace(r.ThreadId) || r.CommentId <= 0
                || r.Assessment is not ("correct" or "incorrect" or "uncertain")
                || r.SettleState is not ("open" or "fixed" or "rejected_by_author")
                || r.OurStance is not ("agree" or "disagree" or "n/a")
                || string.IsNullOrWhiteSpace(r.Evidence) || string.IsNullOrWhiteSpace(r.Reply)
                || (r.Resolve && !r.CanResolve)
                || (r.React && r.Assessment != "correct"))
            || doc.Responses.Select(r => r.ThreadId).Distinct(StringComparer.Ordinal).Count() != doc.Responses.Count
            || doc.Responses.Select(r => r.CommentId).Distinct().Count() != doc.Responses.Count)
            throw new InvalidDataException("Invalid or duplicate thread-response drafts. Check IDs, evidence, assessment, and proposed actions.");
        return doc;
    }
}
