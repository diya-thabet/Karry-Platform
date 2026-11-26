using System.Text.Json;
using Karry.BuildingBlocks.Persistence;
using Karry.Modules.Notifications.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Karry.Modules.Notifications.Infrastructure;

/// <summary>
/// Schema <c>notifications</c> (tenant-owned, RLS): one row per notice and person (ADR-0039 §1), the company's rule
/// switches and each person's channel choices (§2).
/// </summary>
public sealed class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "notifications";

    public override string Schema => SchemaName;

    public DbSet<Notification> Notifications => Set<Notification>();

    public DbSet<NoticeRuleSetting> RuleSettings => Set<NoticeRuleSetting>();

    public DbSet<NoticePreference> Preferences => Set<NoticePreference>();

    public DbSet<EmailDelivery> EmailDeliveries => Set<EmailDelivery>();

    protected override void OnModuleModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<Notification>(notification =>
        {
            notification.ToTable("notifications");
            notification.HasKey(n => n.Id);
            notification.Property(n => n.Id).HasConversion(id => id.Value, value => new NotificationId(value)).ValueGeneratedNever();
            notification.Property(n => n.Type).HasMaxLength(60);
            notification.Property(n => n.Link).HasMaxLength(300);
            notification.Property(n => n.Parameters).HasColumnType("jsonb").HasConversion(Json.Converter, Json.Comparer);
            // One notice per event and person: a redelivered event adds nothing (ADR-0039 §1).
            notification.HasIndex(n => new { n.TenantId, n.SourceEventId, n.RecipientId }).IsUnique();
            // A person's list, newest first, and their unread count.
            notification.HasIndex(n => new { n.TenantId, n.RecipientId, n.Id });
            notification.HasIndex(n => new { n.TenantId, n.RecipientId, n.ReadAt });
            notification.HasIndex(n => new { n.TenantId, n.CreatedAt });
        });

        // A company's switch of a default rule (ADR-0039 §2): one row per type, only once it was switched.
        modelBuilder.Entity<NoticeRuleSetting>(setting =>
        {
            setting.ToTable("rule_settings");
            setting.HasKey(s => s.Id);
            setting.Ignore(s => s.DomainEvents);
            setting.Property(s => s.Id).HasConversion(id => id.Value, value => new NoticeRuleSettingId(value)).ValueGeneratedNever();
            setting.Property(s => s.Type).HasMaxLength(60);
            setting.Property(s => s.ChangedBy).HasMaxLength(200);
            setting.HasIndex(s => new { s.TenantId, s.Type }).IsUnique();
        });

        // A person's channel choice for a type (ADR-0039 §2): one row per person, type and channel.
        modelBuilder.Entity<NoticePreference>(preference =>
        {
            preference.ToTable("preferences");
            preference.HasKey(p => new { p.TenantId, p.UserId, p.Type, p.Channel });
            preference.Property(p => p.Type).HasMaxLength(60);
            preference.Property(p => p.Channel).HasMaxLength(20);
        });

        // An e-mail and its attempts (ADR-0039 §4): the person, never the address; the due ones are read every minute.
        modelBuilder.Entity<EmailDelivery>(delivery =>
        {
            delivery.ToTable("email_deliveries");
            delivery.HasKey(d => d.Id);
            delivery.Property(d => d.Id).HasConversion(id => id.Value, value => new EmailDeliveryId(value)).ValueGeneratedNever();
            delivery.Property(d => d.Kind).HasMaxLength(20);
            delivery.Property(d => d.Key).HasMaxLength(200);
            delivery.Property(d => d.Subject).HasMaxLength(200);
            delivery.Property(d => d.Text).HasMaxLength(20_000);
            delivery.Property(d => d.Status).HasConversion<string>().HasMaxLength(10);
            delivery.Property(d => d.LastError).HasMaxLength(40);
            delivery.HasIndex(d => new { d.TenantId, d.Key }).IsUnique();
            delivery.HasIndex(d => new { d.TenantId, d.Status, d.NextAttemptAt });
            delivery.HasIndex(d => new { d.TenantId, d.CreatedAt });
        });
    }

    private static class Json
    {
        public static readonly ValueConverter<IReadOnlyDictionary<string, string>, string> Converter = new(
            value => JsonSerializer.Serialize(value, JsonSerializerOptions.Web),
            json => JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonSerializerOptions.Web)!);

        public static readonly ValueComparer<IReadOnlyDictionary<string, string>> Comparer = new(
            (a, b) => JsonSerializer.Serialize(a, JsonSerializerOptions.Web) == JsonSerializer.Serialize(b, JsonSerializerOptions.Web),
            value => JsonSerializer.Serialize(value, JsonSerializerOptions.Web).GetHashCode(StringComparison.Ordinal),
            value => JsonSerializer.Deserialize<Dictionary<string, string>>(JsonSerializer.Serialize(value, JsonSerializerOptions.Web), JsonSerializerOptions.Web)!);
    }
}

/// <summary>Design-time factory for <c>dotnet ef migrations add</c> (no database connection is opened).</summary>
internal sealed class NotificationsDbContextFactory : IDesignTimeDbContextFactory<NotificationsDbContext>
{
    public NotificationsDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<NotificationsDbContext>().UseNpgsql("Host=design-time;Database=karry").UseSnakeCaseNamingConvention().Options);
}
