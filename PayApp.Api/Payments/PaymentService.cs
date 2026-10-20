using Microsoft.EntityFrameworkCore;
using PayApp.Api.Email;
using PayApp.Core.Data;
using PayApp.Core.Entities;

namespace PayApp.Api.Payments;

public class PaymentService(AppDbContext db, EmailOutbox outbox, ILogger<PaymentService> logger)
{
    public const long PriceKobo = 10_000;

    // Called by both the webhook and the browser callback, possibly at the same time and more than once.
    // The conditional UPDATE means only one caller can move a payment into a given final state,
    // so each email is sent exactly once (idempotency).
    public async Task ApplyAsync(PaystackTransaction transaction, CancellationToken cancellationToken)
    {
        PaymentStatus? newStatus = transaction.Status switch
        {
            "success" => PaymentStatus.Success,
            "failed" => PaymentStatus.Failed,
            "abandoned" => PaymentStatus.Abandoned,
            _ => null,
        };
        if (newStatus is not { } status)
            return;

        var payment = await db.Payments.AsNoTracking().Include(p => p.User)
            .SingleOrDefaultAsync(p => p.Reference == transaction.Reference, cancellationToken);
        if (payment?.User is null)
        {
            logger.LogWarning("Paystack sent an unknown reference {Reference}", transaction.Reference);
            return;
        }

        if (status == PaymentStatus.Success && !AmountMatches(payment, transaction))
        {
            logger.LogError("Amount mismatch for {Reference}: expected {Expected} {Currency}, got {Actual} {ActualCurrency}",
                payment.Reference, payment.AmountKobo, payment.Currency, transaction.Amount, transaction.Currency);
            return;
        }

        DateTimeOffset? paidAt = status == PaymentStatus.Success
            ? (transaction.PaidAt ?? DateTimeOffset.UtcNow).ToUniversalTime()
            : null;

        // A success can overwrite an earlier failure (the user retried on the same checkout);
        // a failure can only overwrite a pending payment.
        var query = status == PaymentStatus.Success
            ? db.Payments.Where(p => p.Id == payment.Id && p.Status != PaymentStatus.Success)
            : db.Payments.Where(p => p.Id == payment.Id && p.Status == PaymentStatus.Pending);

        var updated = await query.ExecuteUpdateAsync(s => s
            .SetProperty(p => p.Status, status)
            .SetProperty(p => p.PaystackTxnId, transaction.Id)
            .SetProperty(p => p.PaidAt, paidAt), cancellationToken);

        if (updated == 0)
            return;

        payment.Status = status;
        payment.PaidAt = paidAt;
        logger.LogInformation("Payment {Reference} is now {Status}", payment.Reference, status);

        var (kind, content) = status == PaymentStatus.Success
            ? (EmailKind.Receipt, EmailTemplates.Receipt(payment.User, payment))
            : (EmailKind.PaymentFailed, EmailTemplates.PaymentFailed(payment.User, payment));

        await outbox.QueueAsync(payment.User, kind, content, cancellationToken);
    }

    private static bool AmountMatches(Payment payment, PaystackTransaction transaction) =>
        transaction.Amount == payment.AmountKobo &&
        string.Equals(transaction.Currency, payment.Currency, StringComparison.OrdinalIgnoreCase);
}
