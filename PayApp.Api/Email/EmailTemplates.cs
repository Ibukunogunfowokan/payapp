using System.Globalization;
using System.Net;
using PayApp.Core.Entities;

namespace PayApp.Api.Email;

public record EmailContent(string Subject, string BodyHtml);

public static class EmailTemplates
{
    public static EmailContent Verification(AppUser user, string confirmLink) => new(
        "Confirm your PayApp email",
        Layout(user, $"""
            <p>Thanks for signing up. Please confirm your email address to activate your account.</p>
            <p><a href="{Encode(confirmLink)}">Confirm my email</a></p>
            """));

    public static EmailContent PasswordReset(AppUser user, string resetToken) => new(
        "Reset your PayApp password",
        Layout(user, $"""
            <p>We received a request to reset your password. Use this reset code:</p>
            <p><code>{Encode(resetToken)}</code></p>
            <p>If you did not ask for this, you can ignore this email.</p>
            """));

    public static EmailContent Receipt(AppUser user, Payment payment) => new(
        $"Payment receipt: {FormatAmount(payment)}",
        Layout(user, $"""
            <p>Your payment was successful. Thank you!</p>
            <p>Amount: <strong>{FormatAmount(payment)}</strong><br>
            Reference: {Encode(payment.Reference)}<br>
            Date: {payment.PaidAt?.ToString("dd MMM yyyy, HH:mm 'UTC'", CultureInfo.InvariantCulture)}</p>
            """));

    public static EmailContent PaymentFailed(AppUser user, Payment payment) => new(
        "Your PayApp payment did not go through",
        Layout(user, $"""
            <p>Your payment of <strong>{FormatAmount(payment)}</strong> was not completed, and you have not been charged.</p>
            <p>Reference: {Encode(payment.Reference)}</p>
            <p>You can try again at any time.</p>
            """));

    private static string Layout(AppUser user, string content) =>
        $"<p>Hi {Encode(user.FullName)},</p>{content}<p>The PayApp team</p>";

    private static string FormatAmount(Payment payment) =>
        payment.Currency == "NGN"
            ? $"₦{payment.AmountKobo / 100m:N2}"
            : $"{payment.AmountKobo / 100m:N2} {payment.Currency}";

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}
