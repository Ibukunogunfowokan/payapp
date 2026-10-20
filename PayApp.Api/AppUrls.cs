using PayApp.Core.Configuration;

namespace PayApp.Api;

public class AppUrls(IConfiguration config)
{
    private string BaseUrl => config.Optional("APP_BASE_URL", "http://localhost:5146").TrimEnd('/');

    public string ConfirmEmail(Guid userId, string token) =>
        $"{BaseUrl}/api/auth/confirm-email?userId={userId}&token={Uri.EscapeDataString(token)}";

    public string PaymentCallback => $"{BaseUrl}/api/payments/callback";
}
