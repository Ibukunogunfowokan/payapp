using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using PayApp.Core.Entities;

namespace PayApp.Core.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<PaymentWebhookEvent> PaymentWebhookEvents => Set<PaymentWebhookEvent>();
    public DbSet<EmailMessage> EmailMessages => Set<EmailMessage>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Payment>(e =>
        {
            e.ToTable("payments");
            e.HasIndex(p => p.Reference).IsUnique();
            e.Property(p => p.Reference).HasMaxLength(100);
            e.Property(p => p.Currency).HasMaxLength(3);
            e.Property(p => p.Status).HasConversion(SnakeCaseEnum<PaymentStatus>()).HasMaxLength(20);
            e.HasOne(p => p.User).WithMany().HasForeignKey(p => p.UserId);
        });

        builder.Entity<PaymentWebhookEvent>(e =>
        {
            e.ToTable("payment_webhook_events");
            e.HasIndex(w => w.Reference);
            e.Property(w => w.EventType).HasMaxLength(100);
            e.Property(w => w.Payload).HasColumnType("jsonb");
        });

        builder.Entity<EmailMessage>(e =>
        {
            e.ToTable("email_messages");
            e.Property(m => m.Kind).HasConversion(SnakeCaseEnum<EmailKind>()).HasMaxLength(20);
            e.Property(m => m.Status).HasConversion(SnakeCaseEnum<EmailStatus>()).HasMaxLength(20);
            e.HasOne<AppUser>().WithMany().HasForeignKey(m => m.UserId);
        });

        foreach (var entity in new[] { typeof(Payment), typeof(PaymentWebhookEvent), typeof(EmailMessage) })
        {
            foreach (var property in builder.Model.FindEntityType(entity)!.GetProperties())
                property.SetColumnName(ToSnakeCase(property.Name));
        }
    }

    private static ValueConverter<T, string> SnakeCaseEnum<T>() where T : struct, Enum =>
        new(v => ToSnakeCase(v.ToString()), v => Enum.Parse<T>(v.Replace("_", ""), true));

    private static string ToSnakeCase(string name) =>
        Regex.Replace(name, "(?<=[a-z0-9])([A-Z])", "_$1").ToLowerInvariant();
}
