using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace InviteApp;

public class Contact
{
    public int Id { get; set; }
    public string Honorific { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    [Required]
    public string Phone { get; set; } = string.Empty;
    /// <summary>
    /// Comma-separated normalized group tags, e.g., "Family,VIP,Bride Side"
    /// </summary>
    public string Groups { get; set; } = string.Empty;
    [MaxLength(150)]
    public string Notes { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<string> GetGroupList() =>
        Groups.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
              .Distinct(StringComparer.OrdinalIgnoreCase)
              .ToList();
}

public class Campaign
{
    public int Id { get; set; }
    [Required]
    public string Name { get; set; } = string.Empty;
    public string MessageTemplate { get; set; } = string.Empty;
    public string MediaPath { get; set; } = string.Empty;
    /// <summary>
    /// Comma-separated selected groups for this campaign (empty = All Contacts)
    /// </summary>
    public string TargetGroups { get; set; } = string.Empty;
    /// <summary>
    /// "groups" or "individuals"
    /// </summary>
    public string TargetMode { get; set; } = "groups";
    /// <summary>
    /// Comma-separated contact IDs when TargetMode == "individuals"
    /// </summary>
    public string TargetContactIds { get; set; } = string.Empty;
    /// <summary>
    /// Draft, Scheduled, Running, Paused, Completed, Cancelled
    /// </summary>
    public string Status { get; set; } = "Draft";
    public DateTime? ScheduledAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? CurrentActivity { get; set; }

    public List<CampaignRecipient> Recipients { get; set; } = new();
}

public class CampaignRecipient
{
    public int Id { get; set; }
    public int CampaignId { get; set; }
    public int? ContactId { get; set; }
    public string Honorific { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string ResolvedMessage { get; set; } = string.Empty;
    /// <summary>
    /// Pending, Sending, Sent, Failed
    /// </summary>
    public string Status { get; set; } = "Pending";
    public string? ErrorMessage { get; set; }
    public DateTime? SentAt { get; set; }
}

public class InviteDbContext : DbContext
{
    public InviteDbContext(DbContextOptions<InviteDbContext> options) : base(options) { }

    public DbSet<Contact> Contacts => Set<Contact>();
    public DbSet<Campaign> Campaigns => Set<Campaign>();
    public DbSet<CampaignRecipient> CampaignRecipients => Set<CampaignRecipient>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Contact>()
            .HasIndex(c => c.Phone)
            .IsUnique();

        modelBuilder.Entity<CampaignRecipient>()
            .HasIndex(r => new { r.CampaignId, r.Phone })
            .IsUnique();

        modelBuilder.Entity<Campaign>()
            .HasMany(c => c.Recipients)
            .WithOne()
            .HasForeignKey(r => r.CampaignId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public static class Helpers
{
    /// <summary>
    /// Rule 2: Strip all non-digits. If length is 10, auto-prepend 91 (India).
    /// </summary>
    public static string NormalizePhone(string? rawPhone)
    {
        if (string.IsNullOrWhiteSpace(rawPhone)) return string.Empty;
        var digits = new string(rawPhone.Where(char.IsDigit).ToArray());
        if (digits.Length == 10)
        {
            digits = "91" + digits;
        }
        return digits;
    }

    /// <summary>
    /// Rule 6: Strip accidental surrounding quotes ("C:\path\card.jpg")
    /// </summary>
    public static string CleanMediaPath(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath)) return string.Empty;
        return rawPath.Trim().Trim('"', '\'').Trim();
    }

    public static string NormalizeGroups(string? rawGroups, IEnumerable<string>? groupArray = null)
    {
        var items = new List<string>();
        if (!string.IsNullOrWhiteSpace(rawGroups))
        {
            items.AddRange(rawGroups.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        if (groupArray != null)
        {
            foreach (var g in groupArray)
            {
                if (!string.IsNullOrWhiteSpace(g))
                    items.AddRange(g.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
        }
        return string.Join(",", items.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    public static string NormalizeNotes(string? rawNotes, int maxLength = 150)
    {
        if (string.IsNullOrWhiteSpace(rawNotes)) return string.Empty;
        var trimmed = rawNotes.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    public static string RenderTemplate(string template, string? honorific, string? name, string? phone)
    {
        if (string.IsNullOrEmpty(template)) return string.Empty;
        var h = (honorific ?? string.Empty).Trim();
        var n = (name ?? string.Empty).Trim();
        var fullName = string.IsNullOrEmpty(h) ? n : $"{h} {n}".Trim();

        var result = template
            .Replace("{{Honorific}}", h, StringComparison.OrdinalIgnoreCase)
            .Replace("{Honorific}", h, StringComparison.OrdinalIgnoreCase)
            .Replace("{{Name}}", n, StringComparison.OrdinalIgnoreCase)
            .Replace("{Name}", n, StringComparison.OrdinalIgnoreCase)
            .Replace("{{FullName}}", fullName, StringComparison.OrdinalIgnoreCase)
            .Replace("{FullName}", fullName, StringComparison.OrdinalIgnoreCase)
            .Replace("{{Phone}}", phone ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{Phone}", phone ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        // Resolve {Spintax|Options} blocks randomly per recipient
        result = System.Text.RegularExpressions.Regex.Replace(
            result,
            @"\{([^{}]+\|[^{}]+)\}",
            match =>
            {
                var options = match.Groups[1].Value.Split('|');
                return options[Random.Shared.Next(options.Length)];
            });

        return result.Trim();
    }
}
