namespace PayApp.Core.Entities;

public class PaymentWebhookEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? Reference { get; set; }
    public string EventType { get; set; } = "";
    public string Payload { get; set; } = "";
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
}
