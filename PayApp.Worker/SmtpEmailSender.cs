using System.Net;
using System.Net.Mail;
using PayApp.Core.Configuration;
using PayApp.Core.Email;

namespace PayApp.Worker;

public class SmtpEmailSender(IConfiguration config) : IEmailSender
{
    public async Task SendAsync(string to, string subject, string bodyHtml, CancellationToken cancellationToken)
    {
        using var client = new SmtpClient(config.Required("EMAIL_SMTP_HOST"), int.Parse(config.Required("EMAIL_SMTP_PORT")))
        {
            EnableSsl = bool.Parse(config.Optional("EMAIL_SMTP_ENABLE_SSL", "false")),
        };

        if (config["EMAIL_SMTP_USER"] is { Length: > 0 } user)
            client.Credentials = new NetworkCredential(user, config.Required("EMAIL_API_KEY"));

        using var message = new MailMessage(config.Required("EMAIL_FROM_ADDRESS"), to, subject, bodyHtml)
        {
            IsBodyHtml = true,
        };

        await client.SendMailAsync(message, cancellationToken);
    }
}
