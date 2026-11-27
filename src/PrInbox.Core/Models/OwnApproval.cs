namespace PrInbox.Core.Models;

/// <summary>An active approval by the authenticated identity. A null SHA means the platform cannot verify the approved commit.</summary>
public sealed record OwnApproval(string Login, string? HeadSha);
