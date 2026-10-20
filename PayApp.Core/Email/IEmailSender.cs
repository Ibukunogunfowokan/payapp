namespace PayApp.Core.Email;

public interface IEmailSender
{
    Task SendAsync(string to, string subject, string bodyHtml, CancellationToken cancellationToken);
}
