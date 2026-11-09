using System.Text.Json;
using System.Text.Json.Serialization;
using Karry.BuildingBlocks.Persistence;
using Karry.Modules.Billing.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Karry.Modules.Billing.Infrastructure;

/// <summary>
/// Schema <c>billing</c>: the plan catalogue (platform data: no tenant_id, no RLS; see global-reference-tables) and,
/// subscriptions, manual invoices and daily usage snapshots (tenant-owned, row-level security). ADR-0023.
/// </summary>
public sealed class BillingDbContext(DbContextOptions<BillingDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "billing";

    /// <summary>camelCase, enums as lower-case strings, like the API.</summary>
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public override string Schema => SchemaName;

    public DbSet<Plan> Plans => Set<Plan>();

    public DbSet<Subscription> Subscriptions => Set<Subscription>();

    public DbSet<Invoice> Invoices => Set<Invoice>();

    public DbSet<UsageSnapshot> UsageSnapshots => Set<UsageSnapshot>();

    protected override void OnModuleModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<Plan>(plan =>
        {
            plan.ToTable("plans");
            plan.HasKey(p => p.Id);
            plan.Property(p => p.Id).HasConversion(id => id.Value, value => new PlanId(value)).ValueGeneratedNever();
            plan.Property(p => p.Code).HasMaxLength(40);
            plan.HasIndex(p => new { p.Code, p.Version }).IsUnique();
            plan.Property(p => p.Name).HasMaxLength(Plan.NameMaxLength);
            plan.Property(p => p.Features).HasColumnType("jsonb").HasConversion(
                features => JsonSerializer.Serialize(features, Json),
                json => JsonSerializer.Deserialize<List<string>>(json, Json)!,
                Immutable<IReadOnlyList<string>>());
            plan.Property(p => p.Limits).HasColumnType("jsonb").HasConversion(
                limits => JsonSerializer.Serialize(limits, Json),
                json => JsonSerializer.Deserialize<Dictionary<string, int>>(json, Json)!,
                Immutable<IReadOnlyDictionary<string, int>>());
            plan.Property(p => p.Prices).HasColumnType("jsonb").HasConversion(
                prices => JsonSerializer.Serialize(prices, Json),
                json => JsonSerializer.Deserialize<List<PlanPrice>>(json, Json)!,
                Immutable<IReadOnlyList<PlanPrice>>());
            plan.Ignore(p => p.DomainEvents);
            plan.Ignore(p => p.AggregateKey);
        });

        modelBuilder.Entity<Subscription>(subscription =>
        {
            subscription.ToTable("subscriptions");
            subscription.HasKey(s => s.Id);
            subscription.Property(s => s.Id).HasConversion(id => id.Value, value => new SubscriptionId(value)).ValueGeneratedNever();
            subscription.Property(s => s.Status).HasConversion<string>().HasMaxLength(12);
            subscription.Property(s => s.Terms).HasColumnType("jsonb").HasConversion(
                terms => JsonSerializer.Serialize(terms, Json),
                json => JsonSerializer.Deserialize<SubscriptionTerms>(json, Json)!,
                Immutable<SubscriptionTerms>());
            // The record version was a shadow property (P1.11) whose conflict nobody caught (a 500, or a "failed" lifecycle
            // run); now the aggregate carries it, the store reports it and the API answers 409 (H-C1).
            subscription.Property(s => s.Version).IsRecordVersion();

            // One subscription per tenant (ADR-0023).
            subscription.HasIndex(s => s.TenantId).IsUnique();
            subscription.Ignore(s => s.DomainEvents);
            subscription.Ignore(s => s.AggregateKey);
        });

        modelBuilder.Entity<Invoice>(invoice =>
        {
            invoice.ToTable("invoices");
            invoice.HasKey(i => i.Id);
            invoice.Property(i => i.Id).HasConversion(id => id.Value, value => new InvoiceId(value)).ValueGeneratedNever();
            invoice.Property(i => i.SubscriptionId).HasConversion(id => id.Value, value => new SubscriptionId(value));
            invoice.Property(i => i.Number).HasMaxLength(Invoice.NumberMaxLength);
            invoice.Property(i => i.Amount).HasPrecision(19, 4);
            invoice.Property(i => i.Currency).HasMaxLength(3).IsFixedLength();
            invoice.HasIndex(i => new { i.TenantId, i.Number }).IsUnique();
            invoice.HasOne<Subscription>().WithMany().HasForeignKey(i => i.SubscriptionId).OnDelete(DeleteBehavior.Restrict);
            invoice.Ignore(i => i.Total);
            invoice.Ignore(i => i.DomainEvents);
            invoice.Ignore(i => i.AggregateKey);
        });

        modelBuilder.Entity<UsageSnapshot>(snapshot =>
        {
            snapshot.ToTable("usage_snapshots");
            snapshot.HasKey(s => new { s.TenantId, s.Metric, s.Day });
            snapshot.Property(s => s.Metric).HasMaxLength(40);
            snapshot.Ignore(s => s.State);
        });
    }

    /// <summary>Immutable values (a published plan version, a terms record): a change replaces the value, so references compare.</summary>
    private static ValueComparer<T> Immutable<T>()
        where T : class => new((a, b) => ReferenceEquals(a, b), value => value.GetHashCode(), value => value);
}
