using System.Text.Json;
using Karry.BuildingBlocks.Persistence;
using Karry.Modules.Notifications.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Karry.Modules.Notifications.Infrastructure;

/// <summary>Schema <c>notifications</c> (tenant-owned, RLS): one row per notice and person (ADR-0039 §1).</summary>
public sealed class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "notifications";

    public override string Schema => SchemaName;

    public DbSet<Notification> Notifications => Set<Notification>();

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
