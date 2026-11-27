using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Persistence;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Materials.Contracts;
using Karry.Modules.Sales.Domain;
using Karry.SharedKernel.Results;
using Karry.SharedKernel.Tenancy;

namespace Karry.Modules.Sales.Application;

/// <summary>A product as the API receives it; the code is given once, at creation.</summary>
public sealed record ProductInput(string? Code, string? Name, Guid? MaterialId, string? SaleUnit);

/// <summary>Persistence port of products (the caller's unit of work, RLS).</summary>
public interface IProductStore
{
    Task<Product?> FindAsync(ProductId id, CancellationToken cancellationToken);

    Task<IReadOnlyList<Product>> ListAsync(bool includeArchived, CancellationToken cancellationToken);

    /// <summary>Saves with its events; false, with nothing saved, when the code is taken or the product changed since it was loaded.</summary>
    Task<bool> TrySaveAsync(Product product, CancellationToken cancellationToken);

    /// <summary>The products with these codes (imports), tracked so that changes to them are saved by <see cref="TrySaveAllAsync"/>.</summary>
    Task<IReadOnlyList<Product>> FindByCodesAsync(IReadOnlyCollection<string> codes, CancellationToken cancellationToken);

    /// <summary>Adds a new product to the unit of work, saved by <see cref="TrySaveAllAsync"/>.</summary>
    void Add(Product product);

    /// <summary>Saves every pending change in one transaction; false, with nothing saved, on a code or version conflict.</summary>
    Task<bool> TrySaveAllAsync(CancellationToken cancellationToken);
}

/// <summary>US-P3-SAL-02 AC1: what the company sells, each a material fraction with its sale unit.</summary>
public sealed class ProductOperations(IProductStore store, IMaterialDirectory materials, ICallerAccess caller, ITenantContext tenant, TimeProvider clock)
{
    public static readonly Error NotFound = Error.NotFound("sales.product_not_found", "No such product in this company.");

    public static readonly Error CodeTaken = Error.Conflict("sales.product_code_taken", "Another product of the company already has this code.");

    public static readonly Error MaterialNotSold = Error.Validation(
        "sales.product_material_not_sold", "The material is not a fraction of this company that is sold (raw rock, archived or unknown).");

    public async Task<IReadOnlyList<Product>> ListAsync(bool includeArchived, CancellationToken cancellationToken) =>
        [.. (await store.ListAsync(includeArchived, cancellationToken).ConfigureAwait(false)).OrderBy(p => p.Code, StringComparer.Ordinal)];

    public async Task<Result<Product>> GetAsync(ProductId id, CancellationToken cancellationToken) =>
        await store.FindAsync(id, cancellationToken).ConfigureAwait(false) is { } product ? product : NotFound;

    public async Task<Result<Product>> CreateAsync(ProductInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var code = ProductRules.Code(input.Code);
        if (!code.IsSuccess)
        {
            return code.Error;
        }

        var details = await DetailsAsync(input, cancellationToken).ConfigureAwait(false);
        if (!details.IsSuccess)
        {
            return details.Error;
        }

        var product = Product.Create(clock, Tenant, code.Value, details.Value, caller.Subject);
        return await store.TrySaveAsync(product, cancellationToken).ConfigureAwait(false) ? product : CodeTaken;
    }

    /// <summary>New details; with <paramref name="expectedVersion"/> (<c>If-Match</c>) only the edited version changes (H-C1).</summary>
    public async Task<Result<Product>> ChangeAsync(ProductId id, ProductInput input, CancellationToken cancellationToken, uint? expectedVersion = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        var product = await GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (!product.IsSuccess)
        {
            return product;
        }

        if (!RecordVersions.Matches(product.Value.Version, expectedVersion))
        {
            return RecordVersions.Conflict;
        }

        var details = await DetailsAsync(input, cancellationToken, product.Value.MaterialId).ConfigureAwait(false);
        if (!details.IsSuccess)
        {
            return details.Error;
        }

        if (product.Value.Change(clock, details.Value, caller.Subject) && !await store.TrySaveAsync(product.Value, cancellationToken).ConfigureAwait(false))
        {
            return RecordVersions.Conflict;
        }

        return product;
    }

    public Task<Result<Product>> ArchiveAsync(ProductId id, CancellationToken cancellationToken) =>
        TransitionAsync(id, p => p.Archive(clock, caller.Subject), cancellationToken);

    public Task<Result<Product>> RestoreAsync(ProductId id, CancellationToken cancellationToken) =>
        TransitionAsync(id, p => p.Restore(clock, caller.Subject), cancellationToken);

    private async Task<Result<Product>> TransitionAsync(ProductId id, Func<Product, bool> transition, CancellationToken cancellationToken)
    {
        var product = await GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (product.IsSuccess && transition(product.Value) && !await store.TrySaveAsync(product.Value, cancellationToken).ConfigureAwait(false))
        {
            return RecordVersions.Conflict;
        }

        return product;
    }

    /// <summary>Checked details; <paramref name="keptMaterial"/> (the product's current material) is not checked again, archived or not.</summary>
    private async Task<Result<ProductDetails>> DetailsAsync(ProductInput input, CancellationToken cancellationToken, Guid? keptMaterial = null)
    {
        var details = ProductRules.Details(input.Name, input.MaterialId, input.SaleUnit);
        if (!details.IsSuccess || details.Value.MaterialId == keptMaterial)
        {
            return details;
        }

        // A fraction of this company that is sold (the directory reads under RLS: another company's material is not found).
        var material = await materials.FindAsync(details.Value.MaterialId, cancellationToken).ConfigureAwait(false);
        return material is { IsProduct: true, Archived: false } ? details : MaterialNotSold;
    }

    private TenantId Tenant => tenant.Current ?? throw new InvalidOperationException("Sales needs a tenant.");
}
