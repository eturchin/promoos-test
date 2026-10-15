using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Testcontainers.PostgreSql;

namespace TaskApi.Tests;

public sealed class ApiFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine").Build();
    public string ConnectionString => _postgres.GetConnectionString();
    public async Task InitializeAsync() => await _postgres.StartAsync();
    public async Task DisposeAsync() => await _postgres.DisposeAsync();

    public WebApplicationFactory<Program> CreateApp(ITaskEventPublisher? publisher = null,
        string? rabbitUri = null, CompletionBarrier? barrier = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Tasks", ConnectionString);
            if (rabbitUri is not null)
            {
                builder.UseSetting("RabbitMq:Uri", rabbitUri);
            }

            builder.ConfigureTestServices(services =>
            {
                if (publisher is not null)
                {
                    services.RemoveAll<ITaskEventPublisher>();
                    services.AddSingleton(publisher);
                }
                if (barrier is not null)
                {
                    services.AddDbContext<TaskDbContext>(o => o.AddInterceptors(barrier));
                }
            });
        });
}

public sealed class RecordingPublisher(Action<TaskCompletedEvent>? beforeEnqueue = null) : ITaskEventPublisher
{
    public ConcurrentQueue<TaskCompletedEvent> Messages { get; } = new();
    public void Enqueue(TaskCompletedEvent message)
    {
        beforeEnqueue?.Invoke(message);
        Messages.Enqueue(message);
    }
}

// Hold both requests immediately before UPDATE, guaranteeing the same xmin was read.
public sealed class CompletionBarrier : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
{
    private int _arrived;
    private readonly TaskCompletionSource _bothArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context!.ChangeTracker.Entries<TaskItem>().Any(e => e.State == EntityState.Modified))
        {
            if (Interlocked.Increment(ref _arrived) == 2)
            {
                _bothArrived.TrySetResult();
            }

            await _bothArrived.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }
        return result;
    }
}
