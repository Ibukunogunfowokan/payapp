using Microsoft.AspNetCore.Identity;

namespace PayApp.Core.Entities;

public class AppUser : IdentityUser<Guid>
{
    public string FullName { get; set; } = "";
    public bool MarketingOptIn { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
