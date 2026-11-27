using System.Globalization;
using System.Text.Json;
using Karry.BuildingBlocks.Persistence;
using Karry.Modules.Sales.Domain;
using Karry.SharedKernel.Monetary;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Karry.Modules.Sales.Infrastructure;

/// <summary>
/// Schema <c>sales</c> (tenant-owned, RLS): customers (ADR-0042). Addresses, contacts and allowed sites are small lists,
/// always read and written whole with their customer: one JSON document each. The credit limit is stored as exact text
/// ("1500.00 EUR"), never through a floating-point column.
/// </summary>
public sealed class SalesDbContext(DbContextOptions<SalesDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "sales";

    public override string Schema => SchemaName;

    public DbSet<Customer> Customers => Set<Customer>();

    protected override void OnModuleModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<Customer>(customer =>
        {
            customer.ToTable("customers", t => t.HasCheckConstraint("ck_customers_blocked", "(status = 'Blocked') = (blocked_reason IS NOT NULL)"));
            customer.HasKey(c => c.Id);
            customer.Property(c => c.Id).HasConversion(id => id.Value, value => new CustomerId(value)).ValueGeneratedNever();
            customer.Property(c => c.Code).HasMaxLength(CustomerRules.CodeMaxLength);
            customer.HasIndex(c => new { c.TenantId, c.Code }).IsUnique();
            customer.Property(c => c.LegalName).HasMaxLength(CustomerRules.NameMaxLength);
            customer.Property(c => c.TaxId).HasMaxLength(CustomerRules.TaxIdMaxLength);
            customer.Property(c => c.Basis).HasConversion<string>().HasMaxLength(30);
            customer.Property(c => c.Status).HasConversion<string>().HasMaxLength(20);
            customer.Property(c => c.BlockedReason).HasMaxLength(CustomerRules.ReasonMaxLength);
            customer.Property(c => c.Addresses).HasColumnType("jsonb").HasConversion(Json<CustomerAddress>.Converter, Json<CustomerAddress>.Comparer);
            customer.Property(c => c.Contacts).HasColumnType("jsonb").HasConversion(Json<CustomerContact>.Converter, Json<CustomerContact>.Comparer);
            customer.Property(c => c.AllowedSiteIds).HasColumnType("jsonb").HasConversion(Json<Guid>.Converter, Json<Guid>.Comparer);
            customer.Property(c => c.CreditLimit).HasMaxLength(40).HasConversion(MoneyText.Converter);
            customer.Property(c => c.Version).IsRecordVersion();
            customer.Ignore(c => c.Details);
            customer.Ignore(c => c.DomainEvents);
            customer.Ignore(c => c.AggregateKey);
        });
    }

    /// <summary>A small list as one JSON document, compared by its JSON (records inside compare by value).</summary>
    private static class Json<T>
    {
        public static readonly ValueConverter<IReadOnlyList<T>, string> Converter = new(list => Write(list), json => Read(json));

        public static readonly ValueComparer<IReadOnlyList<T>> Comparer = new(
            (a, b) => Write(a!) == Write(b!), list => Write(list).GetHashCode(StringComparison.Ordinal), list => Read(Write(list)));

        private static string Write(IReadOnlyList<T> list) => JsonSerializer.Serialize(list);

        private static List<T> Read(string json) => JsonSerializer.Deserialize<List<T>>(json) ?? [];
    }

    /// <summary>Money as "amount currency" (invariant culture): exact, readable, one column.</summary>
    private static class MoneyText
    {
        public static readonly ValueConverter<Money?, string?> Converter = new(money => Write(money), text => Read(text));

        private static string? Write(Money? money) =>
            money is { } value ? string.Create(CultureInfo.InvariantCulture, $"{value.Amount} {value.Currency.Code}") : null;

        private static Money? Read(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            var parts = text.Split(' ');
            return Money.Of(decimal.Parse(parts[0], NumberStyles.Number, CultureInfo.InvariantCulture), parts[1]);
        }
    }
}

/// <summary>Design-time factory for <c>dotnet ef migrations add</c> (no database connection is opened).</summary>
internal sealed class SalesDbContextFactory : IDesignTimeDbContextFactory<SalesDbContext>
{
    public SalesDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<SalesDbContext>().UseNpgsql("Host=design-time;Database=karry").UseSnakeCaseNamingConvention().Options);
}
