namespace TaskApi.Infrastructure.Messaging;

public sealed class RabbitMqOptions
{
    public string Uri { get; init; } = "amqp://guest:guest@localhost:5672/";
}
