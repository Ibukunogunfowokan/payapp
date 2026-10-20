using Microsoft.EntityFrameworkCore;
using PayApp.Core.Configuration;
using PayApp.Core.Data;
using PayApp.Core.Entities;
using PayApp.Core.Messaging;
using RabbitMQ.Client;

namespace PayApp.Worker;

// If the API saved an email row but RabbitMQ was down at that moment, the id never reached the queue.
// This re-publishes those stuck rows. A duplicate is harmless: the consumer skips rows already marked sent.
public class OutboxSweeper(IConfiguration config, IServiceScopeFactory scopes, ILogger<OutboxSweeper> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan StuckAfter = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Outbox sweep failed; will try again in {Interval}", Interval);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var cutoff = DateTimeOffset.UtcNow - StuckAfter;
        var stuckIds = await db.EmailMessages
            .Where(m => m.Status == EmailStatus.Queued && m.Attempts == 0 && m.CreatedAt < cutoff)
            .Select(m => m.Id)
            .ToListAsync(cancellationToken);

        if (stuckIds.Count == 0)
            return;

        await using var connection = await config.RabbitMqConnectionFactory().CreateConnectionAsync(cancellationToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);
        await EmailQueue.DeclareAsync(channel, cancellationToken);

        foreach (var id in stuckIds)
            await EmailQueue.PublishAsync(channel, EmailQueue.Name, id, cancellationToken);

        logger.LogInformation("Re-published {Count} stuck email(s)", stuckIds.Count);
    }
}
