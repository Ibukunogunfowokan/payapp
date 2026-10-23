using System.Text.Json;
using PayApp.Api.Integrations;

namespace PayApp.Api.Payments;

public class DuctapePaystackGateway(DuctapeService ductape, ILogger<DuctapePaystackGateway> logger) : IPaystackGateway
{
    private const string App = "payapp-paystack";

    public async Task<string> InitializeAsync(string email, long amountKobo, string reference, string callbackUrl,
        CancellationToken cancellationToken)
    {
        var data = await RunAsync<InitializeData>("initialize_transaction", new Dictionary<string, object?>
        {
            ["body:email"] = email,
            ["body:amount"] = amountKobo,
            ["body:currency"] = "NGN",
            ["body:reference"] = reference,
            ["body:callback_url"] = callbackUrl,
        }, cancellationToken);

        return data.AuthorizationUrl;
    }

    public Task<PaystackTransaction> VerifyAsync(string reference, CancellationToken cancellationToken) =>
        RunAsync<PaystackTransaction>("verify_transaction", new Dictionary<string, object?>
        {
            ["params:reference"] = reference,
        }, cancellationToken);

    private async Task<T> RunAsync<T>(string action, Dictionary<string, object?> input, CancellationToken cancellationToken)
    {
        Dictionary<string, object?> result;
        try
        {
            var client = await ductape.GetClientAsync(cancellationToken);
            var request = new Dictionary<string, object?>
            {
                ["product"] = ductape.Product, ["env"] = ductape.Env, ["app"] = App, ["action"] = action, ["input"] = input,
            };

            logger.LogInformation("Ductape request: {Request}", JsonSerializer.Serialize(request));
            result = await client.Api.RunAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new PaystackException($"Ductape could not run '{action}': {ex.InnerException?.Message ?? ex.Message}");
        }

        return Unwrap<T>(action, result);
    }

    // Ductape answers { process_id, status, data }, and `data` is Paystack's own { status, message, data } body.
    private static T Unwrap<T>(string action, Dictionary<string, object?> result)
    {
        var json = JsonSerializer.SerializeToElement(result);

        if (json.TryGetProperty("status", out var ran) && ran.ValueKind == JsonValueKind.False)
            throw new PaystackException($"Ductape reported '{action}' as failed");

        var body = json.TryGetProperty("data", out var paystack)
            ? paystack.Deserialize<PaystackResponse<T>>(PaystackClient.Json)
            : null;

        if (body is not { Status: true, Data: not null })
            throw new PaystackException(body?.Message ?? $"Ductape returned no data for '{action}'");

        return body.Data;
    }
}
