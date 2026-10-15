namespace TaskApi.Tests.Unit;

[Trait("Category", "Unit")]
public sealed class ValidatorTests
{
    private readonly CreateTaskValidator _createValidator = new();
    private readonly GetTasksValidator _queryValidator = new();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\n")]
    public void CreateValidator_RejectsMissingOrWhitespaceTitle(string? title)
    {
        var result = _createValidator.Validate(new CreateTaskRequest(title));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.PropertyName == nameof(CreateTaskRequest.Title));
    }

    [Fact]
    public void CreateValidator_EnforcesLengthAndEnum()
    {
        Assert.True(_createValidator.Validate(new CreateTaskRequest(new string('a', 200))).IsValid);
        Assert.False(_createValidator.Validate(new CreateTaskRequest(new string('a', 201))).IsValid);
        Assert.False(_createValidator.Validate(new CreateTaskRequest("Test", (Priority)99)).IsValid);
    }

    [Fact]
    public void QueryValidator_AcceptsOptionalPaginationAndBoundaryValues()
    {
        Assert.True(_queryValidator.Validate(new GetTasksRequest()).IsValid);
        Assert.True(_queryValidator.Validate(new GetTasksRequest { Page = 1, PageSize = 1 }).IsValid);
        Assert.True(_queryValidator.Validate(new GetTasksRequest { Page = 1_000_000, PageSize = 100 }).IsValid);
        Assert.True(_queryValidator.Validate(new GetTasksRequest { Priority = Priority.High }).IsValid);
    }

    [Fact]
    public void QueryValidator_RejectsInvalidPagingEnumsAndSearch()
    {
        Assert.False(_queryValidator.Validate(new GetTasksRequest { Page = 0 }).IsValid);
        Assert.False(_queryValidator.Validate(new GetTasksRequest { Page = 1_000_001 }).IsValid);
        Assert.False(_queryValidator.Validate(new GetTasksRequest { PageSize = 101 }).IsValid);
        Assert.False(_queryValidator.Validate(new GetTasksRequest { Priority = (Priority)99 }).IsValid);
        Assert.False(_queryValidator.Validate(new GetTasksRequest { SortBy = (TaskSortBy)99 }).IsValid);
        Assert.False(_queryValidator.Validate(new GetTasksRequest { SortDirection = (SortDirection)99 }).IsValid);
        Assert.False(_queryValidator.Validate(new GetTasksRequest { Search = new string('a', 201) }).IsValid);
    }
}
