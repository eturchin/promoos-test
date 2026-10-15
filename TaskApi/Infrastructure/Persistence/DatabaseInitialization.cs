using Microsoft.EntityFrameworkCore;

namespace TaskApi.Infrastructure.Persistence;

public static class DatabaseInitialization
{
    public static async Task InitializeDatabaseAsync(this WebApplication app)
    {
        if (!app.Configuration.GetValue("Database:EnsureCreated", true))
        {
            return;
        }

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TaskDbContext>();

        // Sufficient for the exercise; schema upgrades should use EF migrations.
        await db.Database.EnsureCreatedAsync(app.Lifetime.ApplicationStopping);
    }
}
