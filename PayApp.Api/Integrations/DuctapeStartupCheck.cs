namespace PayApp.Api.Integrations;

// Runs once at startup so the logs show whether Ductape and its database link work. Never stops the app.
public class DuctapeStartupCheck(DuctapeService ductape, ILogger<DuctapeStartupCheck> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Run("database", ductape.CheckDatabaseAsync, stoppingToken);
        await Run("payments query", ductape.QueryPaymentsAsync, stoppingToken);
        await Run("Paystack", ductape.CheckPaystackAsync, stoppingToken);
        await Run("RabbitMQ", ductape.CheckRabbitMqAsync, stoppingToken);
    }

    private async Task Run(string name, Func<CancellationToken, Task> check, CancellationToken stoppingToken)
    {
        try
        {
            await check(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ductape: {Check} check failed: {Message}", name, ex.InnerException?.Message ?? ex.Message);
        }
    }
}
