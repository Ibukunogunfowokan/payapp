namespace PayApp.Api.Payments;

// What the payment endpoints need from Paystack. PaystackClient (direct HTTP) and
// DuctapePaystackGateway (through Ductape) both implement it; PAYMENTS_PROVIDER picks one.
public interface IPaystackGateway
{
    Task<string> InitializeAsync(string email, long amountKobo, string reference, string callbackUrl,
        CancellationToken cancellationToken);

    Task<PaystackTransaction> VerifyAsync(string reference, CancellationToken cancellationToken);
}

