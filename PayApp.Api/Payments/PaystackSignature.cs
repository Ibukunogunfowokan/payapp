using System.Security.Cryptography;
using System.Text;

namespace PayApp.Api.Payments;

// Paystack signs every webhook body with HMAC-SHA512 using our secret key.
// Recomputing it proves the request came from Paystack and wasn't altered.
public static class PaystackSignature
{
    public static bool IsValid(byte[] body, string? signatureHeader, string secretKey)
    {
        if (string.IsNullOrEmpty(signatureHeader))
            return false;

        var expected = Convert.ToHexString(HMACSHA512.HashData(Encoding.UTF8.GetBytes(secretKey), body)).ToLowerInvariant();

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(signatureHeader.ToLowerInvariant()));
    }
}
