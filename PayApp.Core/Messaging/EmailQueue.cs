using RabbitMQ.Client;

namespace PayApp.Core.Messaging;

// email-queue        : the Worker consumes from here.
// email-queue.retry  : failed messages wait here, then expire back into email-queue.
// email-queue.dlq    : messages that failed MaxAttempts times end up here for a human to inspect.
public static class EmailQueue
{
    public const string Name = "email-queue";
    public const string Retry = "email-queue.retry";
    public const string DeadLetter = "email-queue.dlq";
    public const int MaxAttempts = 3;
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);

    public static async Task DeclareAsync(IChannel channel, CancellationToken cancellationToken = default)
    {
        await channel.QueueDeclareAsync(DeadLetter, durable: true, exclusive: false, autoDelete: false,
            cancellationToken: cancellationToken);

        await channel.QueueDeclareAsync(Name, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-dead-letter-exchange"] = "",
                ["x-dead-letter-routing-key"] = DeadLetter,
            },
            cancellationToken: cancellationToken);

        await channel.QueueDeclareAsync(Retry, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-message-ttl"] = (int)RetryDelay.TotalMilliseconds,
                ["x-dead-letter-exchange"] = "",
                ["x-dead-letter-routing-key"] = Name,
            },
            cancellationToken: cancellationToken);
    }

    public static ValueTask PublishAsync(IChannel channel, string queue, Guid emailMessageId,
        CancellationToken cancellationToken = default) =>
        channel.BasicPublishAsync(
            exchange: "",
            routingKey: queue,
            mandatory: true,
            basicProperties: new BasicProperties { Persistent = true },
            body: emailMessageId.ToByteArray(),
            cancellationToken: cancellationToken);

    public static Guid ReadId(ReadOnlyMemory<byte> body) => new(body.Span);
}
