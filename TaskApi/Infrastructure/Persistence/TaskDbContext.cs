using Microsoft.EntityFrameworkCore;
using TaskApi.Domain;

namespace TaskApi.Infrastructure.Persistence;

public sealed class TaskDbContext(DbContextOptions<TaskDbContext> options) : DbContext(options)
{
    public DbSet<TaskItem> Tasks => Set<TaskItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var task = modelBuilder.Entity<TaskItem>();
        task.HasKey(t => t.Id);
        task.Property(t => t.Title).IsRequired().HasMaxLength(TaskItem.MaxTitleLength);
        task.Property(t => t.Priority).HasConversion<string>().HasMaxLength(6);
        // Npgsql maps uint + IsRowVersion to PostgreSQL's automatic xmin column.
        task.Property(t => t.RowVersion).IsRowVersion();
        task.ToTable("Tasks", table =>
        {
            table.HasCheckConstraint("CK_Tasks_Title", "length(btrim(\"Title\")) > 0");
            table.HasCheckConstraint("CK_Tasks_CompletedAt",
                "(\"IsCompleted\" AND \"CompletedAt\" IS NOT NULL) OR (NOT \"IsCompleted\" AND \"CompletedAt\" IS NULL)");
        });
    }
}
