namespace LinkApi.Models;

public record LinksPageResponse(
    IReadOnlyList<LinkListItemResponse> Items,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages);
