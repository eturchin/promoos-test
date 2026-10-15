using TaskApi.Contracts;

namespace TaskApi.Application.Tasks;

public sealed record CompleteTaskResult(CompleteTaskStatus Status, TaskResponse? Task = null);
