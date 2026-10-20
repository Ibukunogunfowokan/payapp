using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using PayApp.Api.Email;
using PayApp.Core.Entities;

namespace PayApp.Api.Auth;

public record RegisterRequest(string Email, string Password, string FullName, bool MarketingOptIn);
public record LoginRequest(string Email, string Password);
public record EmailRequest(string Email);
public record ResetPasswordRequest(string Email, string Token, string NewPassword);

public static class AuthEndpoints
{
    private const string CheckInboxMessage = "If that account exists, we've sent an email. Check your inbox.";

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth");

        group.MapPost("/register", Register);
        group.MapGet("/confirm-email", ConfirmEmail);
        group.MapPost("/resend-confirmation", ResendConfirmation);
        group.MapPost("/login", Login);
        group.MapPost("/logout", Logout).RequireAuthorization();
        group.MapPost("/forgot-password", ForgotPassword);
        group.MapPost("/reset-password", ResetPassword);
        group.MapGet("/me", Me).RequireAuthorization();
    }

    private static async Task<IResult> Register(RegisterRequest request, UserManager<AppUser> users,
        EmailOutbox outbox, AppUrls urls, CancellationToken cancellationToken)
    {
        var user = new AppUser
        {
            UserName = request.Email.Trim(),
            Email = request.Email.Trim(),
            FullName = request.FullName.Trim(),
            MarketingOptIn = request.MarketingOptIn,
        };

        var result = await users.CreateAsync(user, request.Password);
        if (!result.Succeeded)
            return ValidationProblem(result);

        await SendVerificationEmail(user, users, outbox, urls, cancellationToken);
        return Results.Ok(new { message = "Account created. Check your email to confirm your address." });
    }

    private static async Task<IResult> ConfirmEmail(Guid userId, string token, UserManager<AppUser> users)
    {
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null)
            return Results.BadRequest(new { message = "Invalid confirmation link." });

        var result = await users.ConfirmEmailAsync(user, DecodeToken(token));
        return result.Succeeded
            ? Results.Ok(new { message = "Email confirmed. You can now log in." })
            : Results.BadRequest(new { message = "This confirmation link is invalid or has expired." });
    }

    private static async Task<IResult> ResendConfirmation(EmailRequest request, UserManager<AppUser> users,
        EmailOutbox outbox, AppUrls urls, CancellationToken cancellationToken)
    {
        var user = await users.FindByEmailAsync(request.Email.Trim());
        if (user is { EmailConfirmed: false })
            await SendVerificationEmail(user, users, outbox, urls, cancellationToken);

        return Results.Ok(new { message = CheckInboxMessage });
    }

    private static async Task<IResult> Login(LoginRequest request, UserManager<AppUser> users,
        SignInManager<AppUser> signIn)
    {
        var user = await users.FindByEmailAsync(request.Email.Trim());
        if (user is null)
            return Results.Problem("Invalid email or password.", statusCode: StatusCodes.Status401Unauthorized);

        var result = await signIn.PasswordSignInAsync(user, request.Password, isPersistent: false, lockoutOnFailure: true);

        if (result.Succeeded)
            return Results.Ok(new { message = "Logged in." });
        if (result.IsLockedOut)
            return Results.Problem("Too many failed attempts. Try again in a few minutes.", statusCode: StatusCodes.Status423Locked);
        if (result.IsNotAllowed)
            return Results.Problem("Please confirm your email before logging in.", statusCode: StatusCodes.Status403Forbidden);

        return Results.Problem("Invalid email or password.", statusCode: StatusCodes.Status401Unauthorized);
    }

    private static async Task<IResult> Logout(SignInManager<AppUser> signIn)
    {
        await signIn.SignOutAsync();
        return Results.Ok(new { message = "Logged out." });
    }

    private static async Task<IResult> ForgotPassword(EmailRequest request, UserManager<AppUser> users,
        EmailOutbox outbox, CancellationToken cancellationToken)
    {
        var user = await users.FindByEmailAsync(request.Email.Trim());
        if (user is { EmailConfirmed: true })
        {
            var token = EncodeToken(await users.GeneratePasswordResetTokenAsync(user));
            await outbox.QueueAsync(user, EmailKind.PasswordReset, EmailTemplates.PasswordReset(user, token), cancellationToken);
        }

        return Results.Ok(new { message = CheckInboxMessage });
    }

    private static async Task<IResult> ResetPassword(ResetPasswordRequest request, UserManager<AppUser> users)
    {
        var user = await users.FindByEmailAsync(request.Email.Trim());
        if (user is null)
            return Results.BadRequest(new { message = "Invalid or expired reset code." });

        var result = await users.ResetPasswordAsync(user, DecodeToken(request.Token), request.NewPassword);
        return result.Succeeded
            ? Results.Ok(new { message = "Password updated. You can now log in." })
            : ValidationProblem(result);
    }

    private static async Task<IResult> Me(ClaimsPrincipal principal, UserManager<AppUser> users)
    {
        var user = await users.GetUserAsync(principal);
        return user is null
            ? Results.Unauthorized()
            : Results.Ok(new { user.Id, user.Email, user.FullName, user.MarketingOptIn, user.CreatedAt });
    }

    private static async Task SendVerificationEmail(AppUser user, UserManager<AppUser> users,
        EmailOutbox outbox, AppUrls urls, CancellationToken cancellationToken)
    {
        var token = EncodeToken(await users.GenerateEmailConfirmationTokenAsync(user));
        var link = urls.ConfirmEmail(user.Id, token);
        await outbox.QueueAsync(user, EmailKind.Verification, EmailTemplates.Verification(user, link), cancellationToken);
    }

    // Identity tokens contain characters like '+' and '/' that break in URLs, so we base64url them.
    private static string EncodeToken(string token) => WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));

    private static string DecodeToken(string token)
    {
        try
        {
            return Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(token));
        }
        catch (FormatException)
        {
            return "";
        }
    }

    private static IResult ValidationProblem(IdentityResult result) =>
        Results.ValidationProblem(result.Errors
            .GroupBy(e => e.Code)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Description).ToArray()));
}
