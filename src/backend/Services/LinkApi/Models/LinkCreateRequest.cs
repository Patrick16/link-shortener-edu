namespace LinkApi.Models;

public class LinkCreateRequest
{
    public required string OriginalLink { get; init; }

    // Optional, registered-users-only custom short code - see LinksController.CreateLink for
    // validation/normalization and the synchronous creation path this triggers.
    public string? Alias { get; init; }

    // Optional TTL in seconds, counted by the frontend (not a target timestamp) - avoids any
    // client/server clock-skew question about "when does this expire" at the API boundary.
    public int? TtlSeconds { get; init; }
}
