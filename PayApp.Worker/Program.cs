using Microsoft.EntityFrameworkCore;
using PayApp.Core.Configuration;
using PayApp.Core.Data;
using PayApp.Core.Email;
using PayApp.Worker;

DotEnv.Load();

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.PostgresConnectionString()));
builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();
builder.Services.AddHostedService<EmailConsumer>();
builder.Services.AddHostedService<OutboxSweeper>();

builder.Build().Run();
