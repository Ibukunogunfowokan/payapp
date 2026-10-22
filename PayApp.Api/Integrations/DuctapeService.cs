using System.Diagnostics;
using PayApp.Core.Configuration;
using DuctapeClient = Ductape.Sdk.Ductape;

namespace PayApp.Api.Integrations;

public class DuctapeService(IConfiguration config, ILogger<DuctapeService> logger) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private DuctapeClient? client;

    public string Product => config.Optional("DUCTAPE_PRODUCT", "spiral-gateway:payments-and-queues");
    public string Env => config.Optional("DUCTAPE_ENV", "snd");
    public string Database => config.Optional("DUCTAPE_DATABASE", "payapp-db");

    // Logs in once and reuses the client, since the SDK refreshes its own token.
    public async Task<DuctapeClient> GetClientAsync(CancellationToken cancellationToken)
    {
        if (client is not null)
            return client;

        await gate.WaitAsync(cancellationToken);
        try
        {
            if (client is not null)
                return client;

            logger.LogInformation("Ductape: logging in (product={Product}, env={Env})", Product, Env);
            var timer = Stopwatch.StartNew();

            var created = await DuctapeClient.CreateAsync(config.Required("DUCTAPE_ACCESS_KEY"), Product, Env,
                cancellationToken: cancellationToken);
            await created.ReadyAsync();

            logger.LogInformation("Ductape: ready in {Elapsed} ms", timer.ElapsedMilliseconds);
            return client = created;
        }
        finally
        {
            gate.Release();
        }
    }

    // Only logs the result keys that are safe to show, in case the response carries the connection URL.
    public async Task CheckDatabaseAsync(CancellationToken cancellationToken)
    {
        var ductape = await GetClientAsync(cancellationToken);
        var request = new Dictionary<string, object?> { ["product"] = Product, ["env"] = Env, ["database"] = Database };

        logger.LogInformation("Ductape: testing connection to database '{Database}'", Database);
        var timer = Stopwatch.StartNew();
        var result = ductape.Database.TestConnection(request);

        logger.LogInformation(
            "Ductape: database '{Database}' answered in {Elapsed} ms (connected={Connected}, version={Version}, latency={Latency}, keys=[{Keys}])",
            Database, timer.ElapsedMilliseconds,
            result.GetValueOrDefault("connected"), result.GetValueOrDefault("version"), result.GetValueOrDefault("latency"),
            string.Join(", ", result.Keys));
    }

    // Counts rows in the payments table through Ductape's own database connection.
    public async Task QueryPaymentsAsync(CancellationToken cancellationToken)
    {
        var ductape = await GetClientAsync(cancellationToken);
        var request = new Dictionary<string, object?>
        {
            ["product"] = Product, ["env"] = Env, ["database"] = Database, ["table"] = "payments",
        };

        logger.LogInformation("Ductape: counting rows in '{Table}' on '{Database}'", "payments", Database);
        var timer = Stopwatch.StartNew();
        var count = ductape.Database.Count(request);
        logger.LogInformation("Ductape: payments table has {Count} rows ({Elapsed} ms)", count, timer.ElapsedMilliseconds);

        request["limit"] = 1;
        var page = ductape.Database.Query(request);
        logger.LogInformation("Ductape: sample query returned keys=[{Keys}], count={Count}",
            string.Join(", ", page.Keys), page.GetValueOrDefault("count"));
    }

    // Read-only Paystack call (list banks) to prove the app, secret and action are wired up.
    public async Task CheckPaystackAsync(CancellationToken cancellationToken)
    {
        var ductape = await GetClientAsync(cancellationToken);
        var request = new Dictionary<string, object>
        {
            ["product"] = Product, ["env"] = Env, ["app"] = "payapp-paystack", ["action"] = "list_banks",
            ["input"] = new Dictionary<string, object> { ["query:country"] = "nigeria", ["query:perPage"] = "1" },
        };

        logger.LogInformation("Ductape: calling Paystack action 'list_banks' (env={Env})", Env);
        var timer = Stopwatch.StartNew();
        var result = await ductape.Actions.RunAsync(request, cancellationToken);
        logger.LogInformation("Ductape: Paystack answered in {Elapsed} ms ({Type}): {Result}",
            timer.ElapsedMilliseconds, result?.GetType().Name, Truncate(System.Text.Json.JsonSerializer.Serialize(result), 400));
    }

    // Asks Ductape to open the registered RabbitMQ broker; no messages are published.
    public async Task CheckRabbitMqAsync(CancellationToken cancellationToken)
    {
        var ductape = await GetClientAsync(cancellationToken);
        var request = new Dictionary<string, object>
        {
            ["product"] = Product, ["env"] = Env, ["broker"] = config.Optional("DUCTAPE_BROKER", "payapp-rabbitmq"),
        };

        logger.LogInformation("Ductape: testing connection to broker '{Broker}'", request["broker"]);
        var timer = Stopwatch.StartNew();
        var result = await ductape.Brokers.TestConnectionAsync(request, cancellationToken);
        logger.LogInformation("Ductape: broker answered in {Elapsed} ms: {Result}",
            timer.ElapsedMilliseconds, Truncate(System.Text.Json.JsonSerializer.Serialize(result), 400));
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "...";

    public void Dispose()
    {
        client?.Dispose();
        gate.Dispose();
    }
}
