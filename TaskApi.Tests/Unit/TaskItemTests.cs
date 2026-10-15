namespace TaskApi.Tests.Unit;

[Trait("Category", "Unit")]
public sealed class TaskItemTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 10, 15, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(Priority.Low)]
    [InlineData(Priority.Medium)]
    [InlineData(Priority.High)]
    public void Create_SetsIdentityAndDefaults_AndTrimsTitle(Priority priority)
    {
        var task = TaskItem.Create("  Buy milk  ", priority, CreatedAt);

        Assert.NotEqual(Guid.Empty, task.Id);
        Assert.Equal("Buy milk", task.Title);
        Assert.Equal(priority, task.Priority);
        Assert.Equal(CreatedAt, task.CreatedAt);
        Assert.False(task.IsCompleted);
        Assert.Null(task.CompletedAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    public void Create_RejectsBlankTitle(string? title)
    {
        Assert.ThrowsAny<ArgumentException>(() => TaskItem.Create(title!, Priority.Medium, CreatedAt));
    }

    [Fact]
    public void Create_RejectsOversizedTitleAndUndefinedPriority()
    {
        Assert.Throws<ArgumentException>(() => TaskItem.Create(new string('a', 201), Priority.Low, CreatedAt));
        Assert.Throws<ArgumentOutOfRangeException>(() => TaskItem.Create("Test", (Priority)99, CreatedAt));
        Assert.Equal(200, TaskItem.Create(new string('a', 200), Priority.Low, CreatedAt).Title.Length);
    }

    [Fact]
    public void TryComplete_ChangesStateExactlyOnce_AndPreservesFirstTimestamp()
    {
        var task = TaskItem.Create("Test", Priority.Medium, CreatedAt);
        var firstCompletion = CreatedAt.AddMinutes(1);

        Assert.True(task.TryComplete(firstCompletion));
        Assert.True(task.IsCompleted);
        Assert.Equal(firstCompletion, task.CompletedAt);
        Assert.False(task.TryComplete(firstCompletion.AddHours(1)));
        Assert.Equal(firstCompletion, task.CompletedAt);
    }

    [Fact]
    public void Timestamps_AreUtcAndTruncatedToPostgresMicroseconds()
    {
        var timestamp = CreatedAt.ToOffset(TimeSpan.FromHours(3)).AddTicks(17);
        var task = TaskItem.Create("Test", Priority.Low, timestamp);
        task.TryComplete(timestamp.AddMinutes(1));

        Assert.Equal(TimeSpan.Zero, task.CreatedAt.Offset);
        Assert.Equal(CreatedAt.AddTicks(10), task.CreatedAt);
        Assert.Equal(task.CreatedAt.AddMinutes(1), task.CompletedAt);
    }
}
