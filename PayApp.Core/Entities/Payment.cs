namespace PayApp.Core.Entities;

public enum PaymentStatus
{
    Pending,
    Success,
    Failed,
    Abandoned,
}

public class Payment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public AppUser? User { get; set; }
    public string Reference { get; set; } = "";
    public long AmountKobo { get; set; }
    public string Currency { get; set; } = "NGN";
    public PaymentStatus Status { get; set; } = PaymentStatus.Pending;
    public long? PaystackTxnId { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
