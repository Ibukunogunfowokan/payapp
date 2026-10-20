using PayApp.Core.Data;
using PayApp.Core.Entities;

namespace PayApp.Api.Email;

public class EmailOutbox(AppDbContext db, EmailQueuePublisher publisher, ILogger<EmailOutbox> logger)
{
    public async Task QueueAsync(AppUser user, EmailKind kind, EmailContent content, CancellationToken cancellationToken)
    {
        var message = new EmailMessage
        {
            UserId = user.Id,
            ToAddress = user.Email!,
            Kind = kind,
            Subject = content.Subject,
            BodyHtml = content.BodyHtml,
        };

        db.EmailMessages.Add(message);
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            await publisher.PublishAsync(message.Id, cancellationToken);
        }
        catch (Exception ex)
        {
            // The row is saved as 'queued', so the email is not lost; it just needs re-publishing later.
            logger.LogError(ex, "Email {EmailId} was saved but could not be published to the queue", message.Id);
        }
    }
}
