using TaskApi.Domain;

namespace TaskApi.Contracts;

public sealed record CreateTaskRequest(string? Title, Priority Priority = Priority.Medium);
