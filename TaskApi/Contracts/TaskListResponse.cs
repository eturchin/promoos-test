namespace TaskApi.Contracts;

public sealed record TaskListResponse(
    IReadOnlyList<TaskResponse> Items,
    int TotalCount,
    int? Page,
    int? PageSize,
    int? TotalPages);
