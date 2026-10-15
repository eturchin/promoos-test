using TaskApi.Domain;

namespace TaskApi.Contracts;

public sealed class GetTasksRequest
{
    public int? Page
    {
        get; init;
    }
    public int? PageSize
    {
        get; init;
    }
    public bool? IsCompleted
    {
        get; init;
    }
    public Priority? Priority
    {
        get; init;
    }
    public string? Search
    {
        get; init;
    }
    public TaskSortBy? SortBy
    {
        get; init;
    }
    public SortDirection? SortDirection
    {
        get; init;
    }

    internal bool HasPagination => Page.HasValue || PageSize.HasValue;
    internal int EffectivePage => Page ?? 1;
    internal int EffectivePageSize => PageSize ?? 20;
}
