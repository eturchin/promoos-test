namespace TaskApi.Tests.Unit;

[Trait("Category", "Unit")]
public sealed class TaskQueryTests
{
    [Theory]
    [InlineData(SortDirection.Asc, Priority.Low, Priority.Medium, Priority.High)]
    [InlineData(SortDirection.Desc, Priority.High, Priority.Medium, Priority.Low)]
    public void PrioritySort_UsesBusinessOrder(SortDirection direction, params Priority[] expected)
    {
        var tasks = CreateTasks();

        var result = tasks.ApplySort(new GetTasksRequest { SortBy = TaskSortBy.Priority, SortDirection = direction });

        Assert.Equal(expected, result.Select(task => task.Priority));
    }

    [Fact]
    public void Filters_CombineStatusAndPriority()
    {
        var tasks = CreateTasks();
        tasks.Single(task => task.Priority == Priority.High).TryComplete(DateTimeOffset.UtcNow);

        var result = tasks.ApplyFilters(new GetTasksRequest { IsCompleted = false, Priority = Priority.Medium });

        Assert.Equal(Priority.Medium, Assert.Single(result).Priority);
    }

    [Fact]
    public void Sort_UsesIdToBreakTies()
    {
        var tasks = CreateTasks();

        var result = tasks.ApplySort(new GetTasksRequest { SortBy = TaskSortBy.CreatedAt });

        Assert.Equal(tasks.OrderBy(task => task.Id).Select(task => task.Id), result.Select(task => task.Id));
    }

    private static IQueryable<TaskItem> CreateTasks()
    {
        var timestamp = new DateTimeOffset(2026, 10, 15, 10, 0, 0, TimeSpan.Zero);

        return new[]
        {
            TaskItem.Create("High", Priority.High, timestamp),
            TaskItem.Create("Low", Priority.Low, timestamp),
            TaskItem.Create("Medium", Priority.Medium, timestamp)
        }.AsQueryable();
    }
}
