using Microsoft.AspNetCore.Mvc;
using TaskApi.Application.Tasks;
using TaskApi.Contracts;

namespace TaskApi.Api;

public static class TaskEndpoints
{
    public static IEndpointRouteBuilder MapTaskEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/tasks").WithTags("Tasks");

        group.MapPost("", CreateAsync)
            .WithName("CreateTask")
            .WithSummary("Create an incomplete task")
            .AddEndpointFilter<ValidationFilter<CreateTaskRequest>>()
            .Produces<TaskResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem();

        group.MapGet("", ListAsync)
            .WithName("ListTasks")
            .WithSummary("List tasks with optional pagination, filtering and sorting")
            .AddEndpointFilter<ValidationFilter<GetTasksRequest>>()
            .Produces<TaskListResponse>()
            .ProducesValidationProblem();

        group.MapGet("/{id}", GetByIdAsync)
            .WithName("GetTask")
            .WithSummary("Get a task by ID")
            .Produces<TaskResponse>()
            .Produces(StatusCodes.Status404NotFound);

        group.MapPut("/{id}/complete", CompleteAsync)
            .WithName("CompleteTask")
            .WithSummary("Complete a task and enqueue its event after commit")
            .Produces<TaskResponse>()
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapDelete("/{id}", DeleteAsync)
            .WithName("DeleteTask")
            .WithSummary("Permanently delete a task")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound);

        return endpoints;
    }

    private static async Task<IResult> CreateAsync(
        CreateTaskRequest request, TaskService service, CancellationToken cancellationToken)
    {
        var task = await service.CreateAsync(request, cancellationToken);
        return Results.CreatedAtRoute("GetTask", new
        {
            id = task.Id
        }, task);
    }

    private static async Task<IResult> ListAsync(
        [AsParameters] GetTasksRequest request, TaskService service, CancellationToken cancellationToken) =>
        Results.Ok(await service.ListAsync(request, cancellationToken));

    private static async Task<IResult> GetByIdAsync(
        Guid id, TaskService service, CancellationToken cancellationToken)
    {
        var task = await service.GetByIdAsync(id, cancellationToken);
        return task is null ? Results.NotFound() : Results.Ok(task);
    }

    private static async Task<IResult> CompleteAsync(
        Guid id, TaskService service, CancellationToken cancellationToken)
    {
        var result = await service.CompleteAsync(id, cancellationToken);

        return result.Status switch
        {
            CompleteTaskStatus.Completed => Results.Ok(result.Task),
            CompleteTaskStatus.NotFound => Results.NotFound(),
            _ => Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Task completion conflict",
                detail: "Task is already completed or was changed or deleted concurrently.")
        };
    }

    private static async Task<IResult> DeleteAsync(
        Guid id, TaskService service, CancellationToken cancellationToken) =>
        await service.DeleteAsync(id, cancellationToken) ? Results.NoContent() : Results.NotFound();
}
