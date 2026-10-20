namespace PayApp.Core.Entities;

public enum EmailKind
{
    Verification,
    PasswordReset,
    Receipt,
    PaymentFailed,
    Promo,
}

public enum EmailStatus
{
    Queued,
    Sent,
    Failed,
}

public class EmailMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? CampaignId { get; set; }
    public Guid UserId { get; set; }
    public string ToAddress { get; set; } = "";
    public EmailKind Kind { get; set; }
    public string Subject { get; set; } = "";
    public string BodyHtml { get; set; } = "";
    public EmailStatus Status { get; set; } = EmailStatus.Queued;
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? SentAt { get; set; }
}
