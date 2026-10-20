using PayApp.Core.Configuration;
using PayApp.Core.Data;
using PayApp.Core.Email;
using PayApp.Core.Entities;
using PayApp.Core.Messaging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace PayApp.Worker;

public class EmailConsumer(IConfiguration config, IServiceScopeFactory scopes, ILogger<EmailConsumer> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var connection = await config.RabbitMqConnectionFactory().CreateConnectionAsync(stoppingToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);

        await EmailQueue.DeclareAsync(channel, stoppingToken);

        // Take one message at a time; the next one is delivered only after we ack this one.
        await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false, stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, delivery) => HandleAsync(channel, delivery, stoppingToken);

        await channel.BasicConsumeAsync(EmailQueue.Name, autoAck: false, consumer, stoppingToken);
        logger.LogInformation("Listening for emails on {Queue}", EmailQueue.Name);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task HandleAsync(IChannel channel, BasicDeliverEventArgs delivery, CancellationToken cancellationToken)
    {
        if (delivery.Body.Length != 16)
        {
            logger.LogError("Received a malformed message; sending it to {Queue}", EmailQueue.DeadLetter);
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: false, cancellationToken);
            return;
        }

        var emailId = EmailQueue.ReadId(delivery.Body);

        try
        {
            await ProcessAsync(channel, delivery.DeliveryTag, emailId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Something outside the email send failed (e.g. the database is down). Put it back and try later.
            logger.LogError(ex, "Unexpected error handling email {EmailId}; requeueing", emailId);
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, cancellationToken);
        }
    }

    private async Task ProcessAsync(IChannel channel, ulong deliveryTag, Guid emailId, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var sender = scope.ServiceProvider.GetRequiredService<IEmailSender>();

        var message = await db.EmailMessages.FindAsync([emailId], cancellationToken);
        if (message is null || message.Status == EmailStatus.Sent)
        {
            await channel.BasicAckAsync(deliveryTag, multiple: false, cancellationToken);
            return;
        }

        message.Attempts++;

        try
        {
            await sender.SendAsync(message.ToAddress, message.Subject, message.BodyHtml, cancellationToken);

            message.Status = EmailStatus.Sent;
            message.SentAt = DateTimeOffset.UtcNow;
            message.LastError = null;
            await db.SaveChangesAsync(cancellationToken);
            await channel.BasicAckAsync(deliveryTag, multiple: false, cancellationToken);

            logger.LogInformation("Sent {Kind} email {EmailId} to {To}", message.Kind, message.Id, message.ToAddress);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            message.LastError = ex.Message;

            if (message.Attempts < EmailQueue.MaxAttempts)
            {
                await db.SaveChangesAsync(cancellationToken);
                await EmailQueue.PublishAsync(channel, EmailQueue.Retry, message.Id, cancellationToken);
                await channel.BasicAckAsync(deliveryTag, multiple: false, cancellationToken);

                logger.LogWarning("Email {EmailId} failed (attempt {Attempt}/{Max}); retrying in {Delay}s: {Error}",
                    message.Id, message.Attempts, EmailQueue.MaxAttempts, EmailQueue.RetryDelay.TotalSeconds, ex.Message);
            }
            else
            {
                message.Status = EmailStatus.Failed;
                await db.SaveChangesAsync(cancellationToken);
                await channel.BasicNackAsync(deliveryTag, multiple: false, requeue: false, cancellationToken);

                logger.LogError("Email {EmailId} failed {Max} times; moved to {Queue}: {Error}",
                    message.Id, EmailQueue.MaxAttempts, EmailQueue.DeadLetter, ex.Message);
            }
        }
    }
}
