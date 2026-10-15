using TaskApi.Domain;

namespace TaskApi.Infrastructure.Messaging;

public sealed record TaskCompletedEvent(Guid TaskId, string Title, DateTimeOffset CompletedAt, Priority Priority);
