using PayApp.Core.Configuration;
using PayApp.Core.Messaging;
using RabbitMQ.Client;

namespace PayApp.Api.Email;

// One long-lived connection for the whole app; opening a connection per request is slow.
public sealed class EmailQueuePublisher(IConfiguration config) : IAsyncDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IConnection? _connection;
    private IChannel? _channel;

    public async Task PublishAsync(Guid emailMessageId, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var channel = await GetChannelAsync(cancellationToken);
            await EmailQueue.PublishAsync(channel, EmailQueue.Name, emailMessageId, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<IChannel> GetChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true })
            return _channel;

        if (_connection is not { IsOpen: true })
            _connection = await config.RabbitMqConnectionFactory().CreateConnectionAsync(cancellationToken);

        var options = new CreateChannelOptions(
            publisherConfirmationsEnabled: true,
            publisherConfirmationTrackingEnabled: true);

        _channel = await _connection.CreateChannelAsync(options, cancellationToken);
        await EmailQueue.DeclareAsync(_channel, cancellationToken);
        return _channel;
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null)
            await _channel.DisposeAsync();
        if (_connection is not null)
            await _connection.DisposeAsync();
    }
}
