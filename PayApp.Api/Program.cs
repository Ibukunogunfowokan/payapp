using System.Net.Http.Headers;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using PayApp.Api;
using PayApp.Api.Auth;
using PayApp.Api.Email;
using PayApp.Api.Payments;
using PayApp.Core.Configuration;
using PayApp.Core.Data;
using PayApp.Core.Entities;

DotEnv.Load();

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(config.PostgresConnectionString()));

builder.Services
    .AddIdentity<AppUser, IdentityRole<Guid>>(options =>
    {
        options.User.RequireUniqueEmail = true;
        options.SignIn.RequireConfirmedEmail = true;
    })
    .AddEntityFrameworkStores<AppDbContext>()
    .AddDefaultTokenProviders();

// This is an API, so return 401/403 instead of redirecting to a login page.
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = "payapp.auth";
    options.Events.OnRedirectToLogin = context =>
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    };
    options.Events.OnRedirectToAccessDenied = context =>
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    };
});

builder.Services.AddAuthorization();

builder.Services.AddSingleton<AppUrls>();
builder.Services.AddSingleton<EmailQueuePublisher>();
builder.Services.AddScoped<EmailOutbox>();
builder.Services.AddScoped<PaymentService>();

builder.Services.AddHttpClient<PaystackClient>(client =>
{
    var secretKey = config.Required("PAYSTACK_SECRET_KEY");
    if (!secretKey.StartsWith("sk_test_"))
        throw new InvalidOperationException("PayApp only runs against Paystack test mode. Use an sk_test_ key.");

    client.BaseAddress = new Uri("https://api.paystack.co/");
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secretKey);
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapAuthEndpoints();
app.MapPaymentEndpoints();

app.Run();
