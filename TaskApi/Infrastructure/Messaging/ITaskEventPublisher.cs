namespace TaskApi.Infrastructure.Messaging;

public interface ITaskEventPublisher
{
    void Enqueue(TaskCompletedEvent message);
}
