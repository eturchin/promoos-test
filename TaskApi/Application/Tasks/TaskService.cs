using Microsoft.EntityFrameworkCore;
using TaskApi.Contracts;
using TaskApi.Domain;
using TaskApi.Infrastructure.Messaging;
using TaskApi.Infrastructure.Persistence;

namespace TaskApi.Application.Tasks;

public sealed class TaskService(
    TaskDbContext db,
    ITaskEventPublisher publisher,
    TimeProvider clock,
    ILogger<TaskService> logger)
{
    public async Task<TaskResponse?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var task = await db.Tasks.AsNoTracking().SingleOrDefaultAsync(task => task.Id == id, cancellationToken);
        return task is null ? null : TaskResponse.From(task);
    }

    public async Task<TaskResponse> CreateAsync(CreateTaskRequest request, CancellationToken cancellationToken)
    {
        var task = TaskItem.Create(request.Title!, request.Priority, clock.GetUtcNow());
        db.Tasks.Add(task);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Task {TaskId} created with priority {Priority}", task.Id, task.Priority);
        return TaskResponse.From(task);
    }

    public async Task<TaskListResponse> ListAsync(GetTasksRequest request, CancellationToken cancellationToken)
    {
        var query = db.Tasks.AsNoTracking().ApplyFilters(request);
        var totalCount = await query.CountAsync(cancellationToken);
        IQueryable<TaskItem> itemsQuery = query.ApplySort(request);

        if (request.HasPagination)
        {
            itemsQuery = itemsQuery
                .Skip((request.EffectivePage - 1) * request.EffectivePageSize)
                .Take(request.EffectivePageSize);
        }

        var items = await itemsQuery.Select(task => new TaskResponse(
            task.Id, task.Title, task.IsCompleted, task.CreatedAt, task.CompletedAt, task.Priority))
            .ToListAsync(cancellationToken);

        return new TaskListResponse(
            items,
            totalCount,
            request.HasPagination ? request.EffectivePage : null,
            request.HasPagination ? request.EffectivePageSize : null,
            request.HasPagination ? (int)Math.Ceiling((double)totalCount / request.EffectivePageSize) : null);
    }

    public async Task<CompleteTaskResult> CompleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var task = await db.Tasks.SingleOrDefaultAsync(task => task.Id == id, cancellationToken);

        if (task is null)
        {
            return new CompleteTaskResult(CompleteTaskStatus.NotFound);
        }

        if (!task.TryComplete(clock.GetUtcNow()))
        {
            logger.LogInformation("Task {TaskId} completion rejected: already completed", id);
            return new CompleteTaskResult(CompleteTaskStatus.Conflict);
        }

        try
        {
            // xmin in the UPDATE predicate makes exactly one concurrent completion succeed.
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogWarning("Task {TaskId} completion rejected: concurrent change", id);
            return new CompleteTaskResult(CompleteTaskStatus.Conflict);
        }

        // Commit first. Request cancellation must not discard the committed event.
        publisher.Enqueue(new TaskCompletedEvent(task.Id, task.Title, task.CompletedAt!.Value, task.Priority));
        logger.LogInformation("Task {TaskId} completed at {CompletedAt}", task.Id, task.CompletedAt);

        return new CompleteTaskResult(CompleteTaskStatus.Completed, TaskResponse.From(task));
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var deleted = await db.Tasks.Where(task => task.Id == id).ExecuteDeleteAsync(cancellationToken) > 0;

        if (deleted)
        {
            logger.LogInformation("Task {TaskId} deleted", id);
        }

        return deleted;
    }
}
