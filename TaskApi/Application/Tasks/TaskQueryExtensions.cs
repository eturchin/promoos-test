using Microsoft.EntityFrameworkCore;
using TaskApi.Contracts;
using TaskApi.Domain;

namespace TaskApi.Application.Tasks;

public static class TaskQueryExtensions
{
    public static IQueryable<TaskItem> ApplyFilters(this IQueryable<TaskItem> query, GetTasksRequest request)
    {
        if (request.IsCompleted is { } isCompleted)
        {
            query = query.Where(task => task.IsCompleted == isCompleted);
        }

        if (request.Priority is { } priority)
        {
            query = query.Where(task => task.Priority == priority);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var search = request.Search.Trim()
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("%", "\\%", StringComparison.Ordinal)
                .Replace("_", "\\_", StringComparison.Ordinal);

            query = query.Where(task => EF.Functions.ILike(task.Title, $"%{search}%", "\\"));
        }

        return query;
    }

    public static IOrderedQueryable<TaskItem> ApplySort(this IQueryable<TaskItem> query, GetTasksRequest request)
    {
        var descending = request.SortDirection == SortDirection.Desc;
        var ordered = request.SortBy switch
        {
            TaskSortBy.Title => descending ? query.OrderByDescending(task => task.Title) : query.OrderBy(task => task.Title),
            TaskSortBy.CompletedAt => descending
                ? query.OrderBy(task => task.CompletedAt == null).ThenByDescending(task => task.CompletedAt)
                : query.OrderBy(task => task.CompletedAt == null).ThenBy(task => task.CompletedAt),
            // Priority is stored as text, so sorting must use explicit numeric ranks.
            TaskSortBy.Priority => descending
                ? query.OrderByDescending(task => task.Priority == Priority.High ? 2 : task.Priority == Priority.Medium ? 1 : 0)
                : query.OrderBy(task => task.Priority == Priority.High ? 2 : task.Priority == Priority.Medium ? 1 : 0),
            _ => descending ? query.OrderByDescending(task => task.CreatedAt) : query.OrderBy(task => task.CreatedAt)
        };

        return ordered.ThenBy(task => task.Id);
    }
}
