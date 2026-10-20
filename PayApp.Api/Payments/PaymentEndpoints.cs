using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using PayApp.Core.Configuration;
using PayApp.Core.Data;
using PayApp.Core.Entities;

namespace PayApp.Api.Payments;

public static class PaymentEndpoints
{
    public static void MapPaymentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/payments").WithTags("Payments");

        group.MapPost("/", StartPayment).RequireAuthorization();
        group.MapGet("/", ListMyPayments).RequireAuthorization();
        group.MapGet("/callback", Callback);

        app.MapPost("/api/paystack/webhook", Webhook).WithTags("Paystack");
    }

    private static async Task<IResult> StartPayment(ClaimsPrincipal principal, UserManager<AppUser> users,
        AppDbContext db, PaystackClient paystack, AppUrls urls, ILogger<PaystackClient> logger,
        CancellationToken cancellationToken)
    {
        var user = await users.GetUserAsync(principal);
        if (user is null)
            return Results.Unauthorized();

        var payment = new Payment
        {
            UserId = user.Id,
            Reference = $"payapp_{Guid.NewGuid():N}",
            AmountKobo = PaymentService.PriceKobo,
        };
        db.Payments.Add(payment);
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            var checkoutUrl = await paystack.InitializeAsync(user.Email!, payment.AmountKobo, payment.Reference,
                urls.PaymentCallback, cancellationToken);
            return Results.Ok(new { payment.Reference, checkoutUrl });
        }
        catch (PaystackException ex)
        {
            logger.LogError(ex, "Could not start Paystack checkout for {Reference}", payment.Reference);
            return Results.Problem("Could not start the payment. Please try again.", statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static async Task<IResult> ListMyPayments(ClaimsPrincipal principal, UserManager<AppUser> users,
        AppDbContext db, CancellationToken cancellationToken)
    {
        var userId = Guid.Parse(users.GetUserId(principal)!);
        var payments = await db.Payments
            .Where(p => p.UserId == userId)
            .OrderByDescending(p => p.CreatedAt)
            .Select(p => new { p.Reference, p.AmountKobo, p.Currency, Status = p.Status.ToString(), p.PaidAt, p.CreatedAt })
            .ToListAsync(cancellationToken);

        return Results.Ok(payments);
    }

    // Paystack redirects the browser here after checkout. We never trust the browser's word;
    // we ask Paystack directly (server to server) what happened.
    private static async Task<IResult> Callback(string reference, PaystackClient paystack, PaymentService payments,
        AppDbContext db, CancellationToken cancellationToken)
    {
        if (!await db.Payments.AnyAsync(p => p.Reference == reference, cancellationToken))
            return Results.NotFound(new { message = "Payment not found." });

        try
        {
            await payments.ApplyAsync(await paystack.VerifyAsync(reference, cancellationToken), cancellationToken);
        }
        catch (PaystackException)
        {
            return Results.Problem("We couldn't confirm this payment yet. Check your payments list shortly.",
                statusCode: StatusCodes.Status502BadGateway);
        }

        var status = await db.Payments.Where(p => p.Reference == reference)
            .Select(p => p.Status)
            .SingleAsync(cancellationToken);

        return Results.Ok(new { reference, status = status.ToString() });
    }

    private static async Task<IResult> Webhook(HttpRequest request, IConfiguration config, AppDbContext db,
        PaymentService payments, ILogger<PaystackClient> logger, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, cancellationToken);
        var body = buffer.ToArray();

        if (!PaystackSignature.IsValid(body, request.Headers["x-paystack-signature"], config.Required("PAYSTACK_SECRET_KEY")))
        {
            logger.LogWarning("Rejected a webhook with an invalid signature");
            return Results.Unauthorized();
        }

        var paystackEvent = JsonSerializer.Deserialize<PaystackEvent>(body, PaystackClient.Json);

        db.PaymentWebhookEvents.Add(new PaymentWebhookEvent
        {
            Reference = paystackEvent?.Data?.Reference,
            EventType = paystackEvent?.Event ?? "unknown",
            Payload = System.Text.Encoding.UTF8.GetString(body),
        });
        await db.SaveChangesAsync(cancellationToken);

        if (paystackEvent is { Event: "charge.success", Data: not null })
            await payments.ApplyAsync(paystackEvent.Data, cancellationToken);

        return Results.Ok();
    }
}
