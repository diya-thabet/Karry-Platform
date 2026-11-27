using System.Text.RegularExpressions;
using Karry.SharedKernel.Domain;
using Karry.SharedKernel.Identity;
using Karry.SharedKernel.Results;
using Karry.SharedKernel.Tenancy;

namespace Karry.Modules.Sales.Domain;

public readonly record struct ProductId(Guid Value) : IEntityId<ProductId>
{
    public static ProductId From(Guid value) => new(value);

    public override string ToString() => Value.ToString("D");
}

public enum ProductStatus
{
    Active = 1,

    /// <summary>No longer sold: kept for the tickets and price lists that name it, out of the default lists and of new prices.</summary>
    Archived = 2,
}

/// <summary>What a product is, as entered and as stored in the ledger.</summary>
public sealed record ProductDetails(string Name, Guid MaterialId, InvoicingBasis SaleUnit);

[EventType("sales.product_created.v1")]
[AuditSubject("product", nameof(ProductId))]
public sealed record ProductCreated(Guid ProductId, string Code, ProductDetails Product, string? Actor) : DomainEvent;

[EventType("sales.product_changed.v1")]
[AuditSubject("product", nameof(ProductId))]
public sealed record ProductChanged(Guid ProductId, string Code, ProductDetails Before, ProductDetails After, string? Actor) : DomainEvent;

[EventType("sales.product_archived.v1")]
[AuditSubject("product", nameof(ProductId))]
public sealed record ProductArchived(Guid ProductId, string Code, string? Actor) : DomainEvent;

[EventType("sales.product_restored.v1")]
[AuditSubject("product", nameof(ProductId))]
public sealed record ProductRestored(Guid ProductId, string Code, string? Actor) : DomainEvent;

/// <summary>
/// Something the company sells (US-P3-SAL-02 AC1): a material fraction and the unit it is sold in (tonnes, or m³ loose
/// derived from the ticket's mass). Tenant-owned (RLS); archived, never deleted.
/// </summary>
public sealed class Product : AggregateRoot<ProductId>, ITenantOwned
{
    private Product(ProductId id, TenantId tenantId, string code, ProductDetails details, DateTimeOffset at)
        : base(id)
    {
        TenantId = tenantId;
        Code = code;
        Apply(details);
        Status = ProductStatus.Active;
        CreatedAt = at;
        ChangedAt = at;
    }

    // EF Core
    private Product()
        : base(default)
    {
    }

    public TenantId TenantId { get; private set; }

    /// <summary>Short code on tickets and price lists (e.g. "G-8/16"), unique in the company for ever.</summary>
    public string Code { get; private set; } = null!;

    public string Name { get; private set; } = null!;

    /// <summary>The material (a fraction, Materials module) the product is.</summary>
    public Guid MaterialId { get; private set; }

    public InvoicingBasis SaleUnit { get; private set; }

    public ProductStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ChangedAt { get; private set; }

    /// <summary>The record version (PostgreSQL <c>xmin</c>), sent back as <c>If-Match</c> (H-C1).</summary>
    public uint Version { get; private set; }

    public ProductDetails Details => new(Name, MaterialId, SaleUnit);

    public static Product Create(TimeProvider clock, TenantId tenant, string code, ProductDetails details, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(details);
        var at = clock.GetUtcNow();
        var product = new Product(EntityId.New<ProductId>(clock), tenant, code, details, at);
        product.Raise(new ProductCreated(product.Id.Value, code, details, actor) { OccurredAt = at });
        return product;
    }

    /// <summary>New details; false when nothing changed. The code never changes.</summary>
    public bool Change(TimeProvider clock, ProductDetails details, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(details);
        var before = Details;
        if (before == details)
        {
            return false;
        }

        Apply(details);
        ChangedAt = clock.GetUtcNow();
        Raise(new ProductChanged(Id.Value, Code, before, details, actor) { OccurredAt = ChangedAt });
        return true;
    }

    public bool Archive(TimeProvider clock, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (Status == ProductStatus.Archived)
        {
            return false;
        }

        Status = ProductStatus.Archived;
        ChangedAt = clock.GetUtcNow();
        Raise(new ProductArchived(Id.Value, Code, actor) { OccurredAt = ChangedAt });
        return true;
    }

    public bool Restore(TimeProvider clock, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (Status == ProductStatus.Active)
        {
            return false;
        }

        Status = ProductStatus.Active;
        ChangedAt = clock.GetUtcNow();
        Raise(new ProductRestored(Id.Value, Code, actor) { OccurredAt = ChangedAt });
        return true;
    }

    private void Apply(ProductDetails details)
    {
        Name = details.Name;
        MaterialId = details.MaterialId;
        SaleUnit = details.SaleUnit;
    }
}

/// <summary>Product input rules (US-P3-SAL-02 AC1).</summary>
public static partial class ProductRules
{
    public const int CodeMaxLength = 20;
    public const int NameMaxLength = 200;

    public static readonly Error CodeInvalid = Error.Validation(
        "sales.product_code_invalid", "A code is 1 to 20 capital letters, digits, dashes, dots or slashes, starting with a letter or digit.");

    public static readonly Error NameInvalid = Error.Validation("sales.product_name_invalid", $"The name is 1 to {NameMaxLength} characters.");

    public static readonly Error MaterialRequired = Error.Validation("sales.product_material_required", "A product is a material fraction: give its material.");

    public static readonly Error SaleUnitInvalid = Error.Validation("sales.product_sale_unit_invalid", "The sale unit is t or m3-loose.");

    /// <summary>Codes like "G-8/16" or "SAB-0.4": fractions are written with slashes and dots.</summary>
    public static Result<string> Code(string? code)
    {
        var value = code?.Trim().ToUpperInvariant();
        return value is not null && CodePattern().IsMatch(value) ? value : CodeInvalid;
    }

    public static Result<ProductDetails> Details(string? name, Guid? materialId, string? saleUnit)
    {
        var tidyName = name?.Trim();
        if (tidyName is not { Length: > 0 and <= NameMaxLength })
        {
            return NameInvalid;
        }

        if (materialId is not { } material || material == Guid.Empty)
        {
            return MaterialRequired;
        }

        var unit = CustomerRules.Basis(saleUnit ?? "t");
        return unit.IsSuccess ? new ProductDetails(tidyName, material, unit.Value) : SaleUnitInvalid;
    }

    [GeneratedRegex("^[A-Z0-9][A-Z0-9./-]{0,19}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();
}
