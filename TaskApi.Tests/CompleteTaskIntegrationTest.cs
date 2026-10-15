using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;

namespace TaskApi.Tests;

[Trait("Category", "Integration")]
public sealed class CompleteTaskIntegrationTest(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter<Priority>() }
    };

    [Fact]
    public async Task CreateComplete_PersistsTaskThenPublishes_AndRejectsRepeatedCompletion()
    {
        var publisher = new RecordingPublisher(message =>
        {
            // Independent connection must see the committed row at publication time.
            using var db = new TaskDbContext(new DbContextOptionsBuilder<TaskDbContext>()
                .UseNpgsql(fixture.ConnectionString).Options);
            var stored = db.Tasks.AsNoTracking().Single(t => t.Id == message.TaskId);
            Assert.True(stored.IsCompleted);
            Assert.Equal(message.CompletedAt, stored.CompletedAt);
        });
        await using var app = fixture.CreateApp(publisher);
        using var client = app.CreateClient();
        var created = await CreateAsync(client, "Buy milk", "High");
        Assert.False(created.IsCompleted);
        Assert.Null(created.CompletedAt);
        Assert.Equal(Priority.High, created.Priority);

        var response = await client.PutAsync($"/tasks/{created.Id}/complete", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var completed = (await response.Content.ReadFromJsonAsync<TaskResponse>(Json))!;
        Assert.True(completed.IsCompleted);
        Assert.NotNull(completed.CompletedAt);
        Assert.True(completed.CompletedAt >= created.CreatedAt);
        var message = Assert.Single(publisher.Messages);
        Assert.Equal(new TaskCompletedEvent(created.Id, created.Title, completed.CompletedAt.Value, Priority.High), message);
        using var scope = app.Services.CreateScope();
        var saved = await scope.ServiceProvider.GetRequiredService<TaskDbContext>().Tasks.AsNoTracking()
            .SingleAsync(t => t.Id == created.Id);
        Assert.Equal(completed.CompletedAt, saved.CompletedAt);

        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsync($"/tasks/{created.Id}/complete", null)).StatusCode);
        Assert.Single(publisher.Messages);
        var tasks = await client.GetFromJsonAsync<TaskListResponse>("/tasks", Json);
        Assert.Contains(tasks!.Items, t => t.Id == created.Id && t.IsCompleted);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/tasks/{created.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/tasks/{created.Id}")).StatusCode);
    }

    [Fact]
    public async Task ConcurrentCompletion_ExactlyOneSuccess_OneConflict_OneEvent()
    {
        var publisher = new RecordingPublisher();
        await using var app = fixture.CreateApp(publisher, barrier: new CompletionBarrier());
        using var client = app.CreateClient();
        var task = await CreateAsync(client, "Concurrent");
        var results = await Task.WhenAll(
            client.PutAsync($"/tasks/{task.Id}/complete", null),
            client.PutAsync($"/tasks/{task.Id}/complete", null));
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Single(publisher.Messages);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    public async Task InvalidTitle_Returns400(string? title)
    {
        await using var app = fixture.CreateApp(new RecordingPublisher());
        using var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/tasks", new
        {
            title
        })).StatusCode);
    }

    [Fact]
    public async Task ValidationAndDefaults()
    {
        await using var app = fixture.CreateApp(new RecordingPublisher());
        using var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/tasks", new
            {
                title = new string('a', 201)
            })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/tasks", new
            {
                title = "test",
                priority = "Urgent"
            })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/tasks", new
            {
                title = "test",
                priority = 99
            })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsync("/tasks/not-a-guid/complete", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsync($"/tasks/{Guid.NewGuid()}/complete", null)).StatusCode);
        var task = await CreateAsync(client, new string('a', 200));
        Assert.Equal(Priority.Medium, task.Priority);
    }

    [Fact]
    public async Task RabbitUnavailable_TaskStillCompletes()
    {
        await using var app = fixture.CreateApp(rabbitUri: "amqp://guest:guest@127.0.0.1:1/");
        using var client = app.CreateClient();
        var task = await CreateAsync(client, "Broker offline");
        var response = await client.PutAsync($"/tasks/{task.Id}/complete", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await response.Content.ReadFromJsonAsync<TaskResponse>(Json))!.IsCompleted);
    }

    [Fact]
    public async Task Completion_RealRabbitMqReceivesExpectedEvent()
    {
        await using var rabbit = new RabbitMqBuilder().WithImage("rabbitmq:4-alpine").Build();
        await rabbit.StartAsync();
        var factory = new ConnectionFactory { Uri = new Uri(rabbit.GetConnectionString()) };
        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        await channel.ExchangeDeclareAsync(RabbitMqPublisher.Exchange, ExchangeType.Topic, durable: true);
        var queue = await channel.QueueDeclareAsync();
        await channel.QueueBindAsync(queue.QueueName, RabbitMqPublisher.Exchange, RabbitMqPublisher.RoutingKey);

        await using var app = fixture.CreateApp(rabbitUri: rabbit.GetConnectionString());
        using var client = app.CreateClient();
        var task = await CreateAsync(client, "Buy milk", "High");
        var response = await client.PutAsync($"/tasks/{task.Id}/complete", null);
        response.EnsureSuccessStatusCode();
        var completed = (await response.Content.ReadFromJsonAsync<TaskResponse>(Json))!;
        // Stop immediately after accepting the event: shutdown must drain, not discard it.
        var publisher = app.Services.GetRequiredService<RabbitMqPublisher>();
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await publisher.StopAsync(shutdown.Token);
        Assert.True(publisher.ExecuteTask!.IsCompletedSuccessfully);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        BasicGetResult? received;
        while ((received = await channel.BasicGetAsync(queue.QueueName, autoAck: true, timeout.Token)) is null)
        {
            await Task.Delay(50, timeout.Token);
        }

        Assert.Equal(RabbitMqPublisher.RoutingKey, received.RoutingKey);
        using var payload = JsonDocument.Parse(received.Body);
        Assert.Equal("High", payload.RootElement.GetProperty("priority").GetString());
        var message = JsonSerializer.Deserialize<TaskCompletedEvent>(received.Body.Span, Json);
        Assert.Equal(new TaskCompletedEvent(task.Id, task.Title, completed.CompletedAt!.Value, Priority.High), message);
    }

    private static async Task<TaskResponse> CreateAsync(HttpClient client, string title, string? priority = null)
    {
        var response = priority is null
            ? await client.PostAsJsonAsync("/tasks", new
            {
                title
            })
            : await client.PostAsJsonAsync("/tasks", new
            {
                title,
                priority
            });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TaskResponse>(Json))!;
    }
}
