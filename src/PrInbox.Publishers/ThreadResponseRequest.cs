using PrInbox.Core.Reviewing;

namespace PrInbox.Publishers;

public sealed record ThreadResponseRequest(
    string PrUrl,
    string HeadSha,
    ThreadResponseDraft Draft,
    bool Reply,
    bool React,
    bool Resolve);

public sealed record ThreadResponseResult(
    bool Replied,
    bool Reacted,
    bool Resolved,
    string IdentityUsed,
    string? Error = null);
