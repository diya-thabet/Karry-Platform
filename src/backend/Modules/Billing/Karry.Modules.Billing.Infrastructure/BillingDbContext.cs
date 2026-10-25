using System.Text.Json;
using System.Text.Json.Serialization;
using Karry.BuildingBlocks.Persistence;
using Karry.Modules.Billing.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Karry.Modules.Billing.Infrastructure;

/// <summary>
/// Schema <c>billing</c>: the plan catalogue (platform data: no tenant_id, no RLS; see global-reference-tables) and,
/// from slice 2, subscriptions and invoices (tenant-owned, row-level security). ADR-0023.
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
    }

    /// <summary>A published plan version never changes: the value is compared by reference only.</summary>
    private static ValueComparer<T> Immutable<T>()
        where T : class => new((a, b) => ReferenceEquals(a, b), value => value.GetHashCode(), value => value);
}
