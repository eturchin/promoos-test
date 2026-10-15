using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using TaskApi.Domain;

namespace TaskApi.Infrastructure.Messaging;

// Only the background reader owns the RabbitMQ channel.
public sealed class RabbitMqPublisher(IOptions<RabbitMqOptions> options, ILogger<RabbitMqPublisher> logger)
    : BackgroundService, ITaskEventPublisher
{
    public const string Exchange = "task.events";
    public const string RoutingKey = "task.completed";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter<Priority>() }
    };
    private readonly Channel<TaskCompletedEvent> _queue = Channel.CreateBounded<TaskCompletedEvent>(
        new BoundedChannelOptions(1024) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private IConnection? _connection;
    private IChannel? _channel;

    public void Enqueue(TaskCompletedEvent message)
    {
        if (!_queue.Writer.TryWrite(message))
        {
            logger.LogWarning("RabbitMQ queue full or stopped. Unsent event: {Event}",
                JsonSerializer.Serialize(message, JsonOptions));
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var message in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning("Shutdown deadline reached. Unsent event: {Event}",
                        JsonSerializer.Serialize(message, JsonOptions));
                    break;
                }
                var body = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(3));
                    await EnsureConnectedAsync(timeout.Token);
                    await _channel!.BasicPublishAsync(Exchange, RoutingKey, mandatory: false,
                        basicProperties: new BasicProperties
                        {
                            ContentType = "application/json",
                            Persistent = true,
                            MessageId = message.TaskId.ToString(),
                            Type = RoutingKey
                        }, body: body, cancellationToken: timeout.Token);
                    logger.LogInformation("Task completion event {TaskId} published to {Exchange} with {RoutingKey}",
                        message.TaskId, Exchange, RoutingKey);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "RabbitMQ publication failed. Unsent event: {Event}",
                        System.Text.Encoding.UTF8.GetString(body));
                    // Reconnect on next event; failed events are deliberately log-only.
                    await DisposeConnectionAsync();
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Remaining queued events are written to the log below.
        }
        finally
        {
            while (_queue.Reader.TryRead(out var message))
            {
                logger.LogWarning("Shutdown deadline reached. Unsent event: {Event}",
                    JsonSerializer.Serialize(message, JsonOptions));
            }

            await DisposeConnectionAsync();
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_channel is { IsOpen: true })
        {
            return;
        }

        await DisposeConnectionAsync();
        var factory = new ConnectionFactory
        {
            Uri = new Uri(options.Value.Uri),
            AutomaticRecoveryEnabled = false,
            RequestedConnectionTimeout = TimeSpan.FromSeconds(2)
        };
        _connection = await factory.CreateConnectionAsync(ct);
        _channel = await _connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: false, publisherConfirmationTrackingEnabled: false), ct);
        await _channel.ExchangeDeclareAsync(Exchange, ExchangeType.Topic, durable: true,
            autoDelete: false, cancellationToken: ct);
    }

    private async Task DisposeConnectionAsync()
    {
        // Bound the AMQP close handshake; don't wait for an unresponsive broker indefinitely.
        using var closeTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        try
        {
            if (_channel is { IsOpen: true })
            {
                await _channel.CloseAsync(closeTimeout.Token);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "RabbitMQ channel close handshake failed");
        }

        try
        {
            if (_connection is { IsOpen: true })
            {
                await _connection.CloseAsync(TimeSpan.FromMilliseconds(500));
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "RabbitMQ connection close handshake failed");
        }

        // Connection.CloseAsync forcibly closes the socket on timeout. Release resources next.
        try
        {
            if (_channel is not null)
            {
                await _channel.DisposeAsync();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error closing RabbitMQ channel");
        }
        finally
        {
            _channel = null;
        }

        try
        {
            if (_connection is not null)
            {
                await _connection.DisposeAsync();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error closing RabbitMQ connection");
        }
        finally
        {
            _connection = null;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _queue.Writer.TryComplete();
        // Don't cancel the reader yet: allow queued/in-flight publications to drain.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            if (ExecuteTask is not null)
            {
                await ExecuteTask.WaitAsync(deadline.Token);
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("RabbitMQ shutdown drain timed out");
        }
        finally
        {
            await base.StopAsync(cancellationToken);
        }
    }
}
