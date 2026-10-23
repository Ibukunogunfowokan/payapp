using System.Text.Json;

namespace PayApp.Api.Payments;

public record PaystackResponse<T>(bool Status, string? Message, T? Data);
public record InitializeData(string AuthorizationUrl, string AccessCode, string Reference);
public record PaystackTransaction(long Id, string? Status, string? Reference, long Amount, string? Currency, DateTimeOffset? PaidAt);
public record PaystackEvent(string? Event, PaystackTransaction? Data);

public class PaystackException(string message) : Exception(message);

public class PaystackClient(HttpClient http) : IPaystackGateway
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public async Task<string> InitializeAsync(string email, long amountKobo, string reference, string callbackUrl,
        CancellationToken cancellationToken)
    {
        var request = new { Email = email, Amount = amountKobo, Currency = "NGN", Reference = reference, CallbackUrl = callbackUrl };
        using var response = await http.PostAsJsonAsync("transaction/initialize", request, Json, cancellationToken);
        var body = await ReadAsync<InitializeData>(response, cancellationToken);
        return body.AuthorizationUrl;
    }

    public async Task<PaystackTransaction> VerifyAsync(string reference, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync($"transaction/verify/{Uri.EscapeDataString(reference)}", cancellationToken);
        return await ReadAsync<PaystackTransaction>(response, cancellationToken);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadFromJsonAsync<PaystackResponse<T>>(Json, cancellationToken);
        if (!response.IsSuccessStatusCode || body is not { Status: true, Data: not null })
            throw new PaystackException(body?.Message ?? $"Paystack returned {(int)response.StatusCode}");

        return body.Data;
    }
}
