using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TaskApi.Tests;

[Trait("Category", "Integration")]
public sealed class TaskQueryIntegrationTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter<Priority>() }
    };

    [Fact]
    public async Task List_CombinesPaginationFiltersAndPrioritySortInPostgres()
    {
        await using var app = fixture.CreateApp(new RecordingPublisher());
        using var client = app.CreateClient();
        var prefix = Guid.NewGuid().ToString("N");
        var low = await CreateAsync(client, $"{prefix}-A", Priority.Low);
        var high = await CreateAsync(client, $"{prefix}-B", Priority.High);
        var medium = await CreateAsync(client, $"{prefix}-C", Priority.Medium);
        (await client.PutAsync($"/tasks/{high.Id}/complete", null)).EnsureSuccessStatusCode();

        var first = (await client.GetFromJsonAsync<TaskListResponse>(
            $"/tasks?search={prefix}&page=1&pageSize=2&sortBy=Priority&sortDirection=Desc", Json))!;

        Assert.Equal(3, first.TotalCount);
        Assert.Equal(1, first.Page);
        Assert.Equal(2, first.PageSize);
        Assert.Equal(2, first.TotalPages);
        Assert.Equal(new[] { high.Id, medium.Id }, first.Items.Select(task => task.Id));

        var second = (await client.GetFromJsonAsync<TaskListResponse>(
            $"/tasks?search={prefix}&page=2&pageSize=2&sortBy=Priority&sortDirection=Desc", Json))!;
        Assert.Equal(low.Id, Assert.Single(second.Items).Id);

        var all = (await client.GetFromJsonAsync<TaskListResponse>($"/tasks?search={prefix}&sortBy=Title", Json))!;
        Assert.Equal(new[] { low.Id, high.Id, medium.Id }, all.Items.Select(task => task.Id));
        Assert.Null(all.Page);
        Assert.Null(all.PageSize);
        Assert.Null(all.TotalPages);

        var filtered = (await client.GetFromJsonAsync<TaskListResponse>(
            $"/tasks?search={prefix}&isCompleted=false&priority=Medium", Json))!;
        Assert.Equal(medium.Id, Assert.Single(filtered.Items).Id);
        Assert.Equal(1, filtered.TotalCount);

        var completedFirst = (await client.GetFromJsonAsync<TaskListResponse>(
            $"/tasks?search={prefix}&sortBy=CompletedAt&sortDirection=Desc", Json))!;
        Assert.Equal(high.Id, completedFirst.Items[0].Id);

        var emptyPage = (await client.GetFromJsonAsync<TaskListResponse>(
            $"/tasks?search={prefix}&page=99&pageSize=2", Json))!;
        Assert.Empty(emptyPage.Items);
        Assert.Equal(3, emptyPage.TotalCount);

        var byId = await client.GetFromJsonAsync<TaskResponse>($"/tasks/{medium.Id}", Json);
        Assert.Equal(medium, byId);
    }

    [Fact]
    public async Task Search_IsCaseInsensitive_AndTreatsWildcardsLiterally()
    {
        await using var app = fixture.CreateApp(new RecordingPublisher());
        using var client = app.CreateClient();
        var prefix = Guid.NewGuid().ToString("N");
        var literal = await CreateAsync(client, $"{prefix} 100%_Done", Priority.High);
        await CreateAsync(client, $"{prefix} 100XXDone", Priority.Low);

        var search = Uri.EscapeDataString($"{prefix} 100%_done");
        var result = (await client.GetFromJsonAsync<TaskListResponse>($"/tasks?search={search}", Json))!;

        Assert.Equal(literal.Id, Assert.Single(result.Items).Id);
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("page=-1")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=101")]
    [InlineData("page=1000001")]
    [InlineData("sortBy=Unknown")]
    [InlineData("sortBy=99")]
    [InlineData("sortDirection=99")]
    [InlineData("priority=99")]
    [InlineData("isCompleted=maybe")]
    public async Task InvalidQuery_Returns400(string query)
    {
        await using var app = fixture.CreateApp(new RecordingPublisher());
        using var client = app.CreateClient();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/tasks?{query}")).StatusCode);
    }

    [Fact]
    public async Task Swagger_DescribesPublicEndpointsAndResponseSchemas()
    {
        await using var app = fixture.CreateApp(new RecordingPublisher());
        using var client = app.CreateClient();

        using var document = JsonDocument.Parse(await client.GetStringAsync("/swagger/v1/swagger.json"));
        var paths = document.RootElement.GetProperty("paths");

        Assert.True(paths.TryGetProperty("/tasks", out _));
        Assert.True(paths.TryGetProperty("/tasks/{id}/complete", out _));
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        Assert.Equal("string", schemas.GetProperty(nameof(Priority)).GetProperty("type").GetString());
        Assert.True(schemas.TryGetProperty(nameof(TaskListResponse), out _));
        Assert.False(schemas.GetProperty(nameof(TaskResponse)).GetProperty("properties").TryGetProperty("rowVersion", out _));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/swagger/index.html")).StatusCode);
    }

    [Fact]
    public async Task ValidationProblem_ContainsFieldErrorsAndTraceId()
    {
        await using var app = fixture.CreateApp(new RecordingPublisher());
        using var client = app.CreateClient();

        var response = await client.PostAsJsonAsync("/tasks", new
        {
            title = "  "
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("title", out _));
        Assert.Equal(response.Headers.GetValues("X-Trace-Id").Single(),
            problem.RootElement.GetProperty("traceId").GetString());
    }

    private static async Task<TaskResponse> CreateAsync(HttpClient client, string title, Priority priority)
    {
        var response = await client.PostAsJsonAsync("/tasks", new CreateTaskRequest(title, priority), Json);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);

        return (await response.Content.ReadFromJsonAsync<TaskResponse>(Json))!;
    }
}
