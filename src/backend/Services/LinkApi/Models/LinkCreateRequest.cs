namespace LinkApi.Models;

public class LinkCreateRequest
{
    public required string OriginalLink { get; init; }

    // Optional, registered-users-only custom short code - see LinksController.CreateLink for
    // validation/normalization and the synchronous creation path this triggers.
    public string? Alias { get; init; }
}
