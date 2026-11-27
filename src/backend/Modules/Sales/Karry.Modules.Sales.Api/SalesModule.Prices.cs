using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Idempotency;
using Karry.Modules.Sales.Application;
using Karry.Modules.Sales.Contracts;
using Karry.Modules.Sales.Domain;
using Karry.SharedKernel.Authorization;
using Karry.SharedKernel.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Karry.Modules.Sales.Api;

/// <summary>Products (US-P3-SAL-02 AC1), price list versions (AC2) and the price on a date (ADR-0042 §2).</summary>
public sealed partial class SalesModule
{
    private static void MapProducts(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/products", ListProductsAsync)
            .RequirePermission(SalesPermissions.ProductsRead)
            .WithName("ListProducts");

        endpoints.MapGet("/products/{productId:guid}", GetProductAsync)
            .RequirePermission(SalesPermissions.ProductsRead)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("GetProduct");

        endpoints.MapPost("/products", CreateProductAsync)
            .RequirePermission(SalesPermissions.ProductsManage)
            .RequireIdempotencyKey()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("CreateProduct");

        endpoints.MapPut("/products/{productId:guid}", ChangeProductAsync)
            .RequirePermission(SalesPermissions.ProductsManage)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("ChangeProduct");

        endpoints.MapPost("/products/{productId:guid}/archive", ArchiveProductAsync)
            .RequirePermission(SalesPermissions.ProductsManage)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("ArchiveProduct");

        endpoints.MapPost("/products/{productId:guid}/restore", RestoreProductAsync)
            .RequirePermission(SalesPermissions.ProductsManage)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("RestoreProduct");
    }

    private static void MapPriceLists(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/price-lists", ListPriceListsAsync)
            .RequirePermission(SalesPermissions.ProductsRead)
            .WithName("ListPriceLists");

        endpoints.MapGet("/price-lists/{priceListId:guid}", GetPriceListAsync)
            .RequirePermission(SalesPermissions.ProductsRead)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("GetPriceList");

        endpoints.MapPost("/price-lists", CreatePriceListAsync)
            .RequirePermission(SalesPermissions.PricesManage)
            .RequireIdempotencyKey()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("CreatePriceList");

        // Only a version not yet in force (a version in force priced tickets: a correction is a new version).
        endpoints.MapPut("/price-lists/{priceListId:guid}/lines", ChangePriceLinesAsync)
            .RequirePermission(SalesPermissions.PricesManage)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("ChangePriceLines");

        endpoints.MapPost("/price-lists/{priceListId:guid}/withdraw", WithdrawPriceListAsync)
            .RequirePermission(SalesPermissions.PricesManage)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("WithdrawPriceList");

        // The portal's "price on date" preview; the weighbridge reads the same answer through IPriceBook.
        endpoints.MapGet("/prices", PriceAtAsync)
            .RequirePermission(SalesPermissions.ProductsRead)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("GetPriceAt");
    }

    private static async Task<Ok<ProductListResponse>> ListProductsAsync(
        [FromServices] ProductOperations products, CancellationToken cancellationToken, bool includeArchived = false)
    {
        var list = await products.ListAsync(includeArchived, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(new ProductListResponse([.. list.Select(ProductResponse.From)]));
    }

    private static async Task<Results<Ok<ProductResponse>, ProblemHttpResult>> GetProductAsync(
        Guid productId, HttpResponse response, [FromServices] ProductOperations products, CancellationToken cancellationToken) =>
        Respond(await products.GetAsync(new ProductId(productId), cancellationToken).ConfigureAwait(false), response);

    private static async Task<Results<Created<ProductResponse>, ProblemHttpResult>> CreateProductAsync(
        ProductInput request, [FromServices] ProductOperations products, CancellationToken cancellationToken)
    {
        var result = await products.CreateAsync(request, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? TypedResults.Created($"/api/v1/sales/products/{result.Value.Id}", ProductResponse.From(result.Value))
            : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<ProductResponse>, ProblemHttpResult>> ChangeProductAsync(
        Guid productId, ProductInput request, [FromHeader(Name = "If-Match")] string? ifMatch, HttpResponse response,
        [FromServices] ProductOperations products, CancellationToken cancellationToken)
    {
        var expected = RecordVersionHttp.Expected(ifMatch);
        return expected.IsSuccess
            ? Respond(await products.ChangeAsync(new ProductId(productId), request, cancellationToken, expected.Value).ConfigureAwait(false), response)
            : expected.Error.ToProblem();
    }

    private static async Task<Results<Ok<ProductResponse>, ProblemHttpResult>> ArchiveProductAsync(
        Guid productId, HttpResponse response, [FromServices] ProductOperations products, CancellationToken cancellationToken) =>
        Respond(await products.ArchiveAsync(new ProductId(productId), cancellationToken).ConfigureAwait(false), response);

    private static async Task<Results<Ok<ProductResponse>, ProblemHttpResult>> RestoreProductAsync(
        Guid productId, HttpResponse response, [FromServices] ProductOperations products, CancellationToken cancellationToken) =>
        Respond(await products.RestoreAsync(new ProductId(productId), cancellationToken).ConfigureAwait(false), response);

    private static async Task<Ok<PriceListListResponse>> ListPriceListsAsync(
        [FromServices] PriceListOperations prices, CancellationToken cancellationToken, Guid? siteId = null, Guid? customerId = null, bool siteOnly = false)
    {
        var today = await prices.TodayAsync(cancellationToken).ConfigureAwait(false);
        var list = await prices.ListAsync(siteId, customerId, siteOnly, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(new PriceListListResponse([.. list.Select(l => PriceListResponse.From(l, today))]));
    }

    private static async Task<Results<Ok<PriceListResponse>, ProblemHttpResult>> GetPriceListAsync(
        Guid priceListId, HttpResponse response, [FromServices] PriceListOperations prices, CancellationToken cancellationToken) =>
        await RespondAsync(await prices.GetAsync(new PriceListId(priceListId), cancellationToken).ConfigureAwait(false), prices, response, cancellationToken)
            .ConfigureAwait(false);

    private static async Task<Results<Created<PriceListResponse>, ProblemHttpResult>> CreatePriceListAsync(
        PriceListInput request, [FromServices] PriceListOperations prices, CancellationToken cancellationToken)
    {
        var result = await prices.CreateAsync(request, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return result.Error.ToProblem();
        }

        var today = await prices.TodayAsync(cancellationToken).ConfigureAwait(false);
        return TypedResults.Created($"/api/v1/sales/price-lists/{result.Value.Id}", PriceListResponse.From(result.Value, today));
    }

    private static async Task<Results<Ok<PriceListResponse>, ProblemHttpResult>> ChangePriceLinesAsync(
        Guid priceListId, PriceLinesInput request, [FromHeader(Name = "If-Match")] string? ifMatch, HttpResponse response,
        [FromServices] PriceListOperations prices, CancellationToken cancellationToken)
    {
        var expected = RecordVersionHttp.Expected(ifMatch);
        if (!expected.IsSuccess)
        {
            return expected.Error.ToProblem();
        }

        var result = await prices.ChangeLinesAsync(new PriceListId(priceListId), request, cancellationToken, expected.Value).ConfigureAwait(false);
        return await RespondAsync(result, prices, response, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Results<Ok<PriceListResponse>, ProblemHttpResult>> WithdrawPriceListAsync(
        Guid priceListId, HttpResponse response, [FromServices] PriceListOperations prices, CancellationToken cancellationToken) =>
        await RespondAsync(await prices.WithdrawAsync(new PriceListId(priceListId), cancellationToken).ConfigureAwait(false), prices, response, cancellationToken)
            .ConfigureAwait(false);

    /// <summary><c>at</c>: the instant (default now); its date on the company's clock picks the versions in force.</summary>
    private static async Task<Results<Ok<PriceQuoteResponse>, ProblemHttpResult>> PriceAtAsync(
        [FromServices] PriceListOperations prices, TimeProvider clock, CancellationToken cancellationToken, Guid? siteId = null, Guid? productId = null,
        Guid? customerId = null, DateTimeOffset? at = null)
    {
        if (siteId is not { } site || productId is not { } product)
        {
            return Error.Validation("sales.price_query_invalid", "Give siteId and productId (customerId and at are optional).").ToProblem();
        }

        var quote = await prices.QuoteAsync(site, customerId, product, at ?? clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        return quote.IsSuccess ? TypedResults.Ok(PriceQuoteResponse.From(quote.Value)) : quote.Error.ToProblem();
    }

    private static Results<Ok<ProductResponse>, ProblemHttpResult> Respond(Result<Product> result, HttpResponse response)
    {
        if (!result.IsSuccess)
        {
            return result.Error.ToProblem();
        }

        response.SetETag(result.Value.Version);
        return TypedResults.Ok(ProductResponse.From(result.Value));
    }

    private static async Task<Results<Ok<PriceListResponse>, ProblemHttpResult>> RespondAsync(
        Result<PriceList> result, PriceListOperations prices, HttpResponse response, CancellationToken cancellationToken)
    {
        if (!result.IsSuccess)
        {
            return result.Error.ToProblem();
        }

        response.SetETag(result.Value.Version);
        return TypedResults.Ok(PriceListResponse.From(result.Value, await prices.TodayAsync(cancellationToken).ConfigureAwait(false)));
    }
}

public sealed record ProductListResponse(IReadOnlyList<ProductResponse> Items);

/// <summary>A product: <c>SaleUnit</c> is "t" or "m3-loose"; <c>Status</c> "active" or "archived".</summary>
public sealed record ProductResponse(
    Guid Id, string Code, string Name, Guid MaterialId, string SaleUnit, string Status, DateTimeOffset CreatedAt, DateTimeOffset ChangedAt, string Version)
{
    public static ProductResponse From(Product product)
    {
        ArgumentNullException.ThrowIfNull(product);
        return new(product.Id.Value, product.Code, product.Name, product.MaterialId, CustomerRules.BasisCode(product.SaleUnit),
            product.Status.ToString().ToLowerInvariant(), product.CreatedAt, product.ChangedAt, RecordVersionHttp.ETag(product.Version));
    }
}

/// <summary>A line of a version: <c>UnitPrice</c> is the sensitive field <c>sales.prices.unit-price</c>.</summary>
public sealed record PriceLineResponse(Guid ProductId, [property: Sensitive(SalesPermissions.UnitPriceField)] decimal? UnitPrice);

public sealed record PriceListListResponse(IReadOnlyList<PriceListResponse> Items);

/// <summary>
/// A price list version. <c>CustomerId</c> null: the site's prices. <c>Status</c> "active" or "withdrawn";
/// <c>InForce</c>: started on or before today on the company's clock (it then never changes).
/// </summary>
public sealed record PriceListResponse(
    Guid Id, Guid SiteId, Guid? CustomerId, string Currency, DateOnly EffectiveFrom, string Status, bool InForce, IReadOnlyList<PriceLineResponse> Lines,
    DateTimeOffset CreatedAt, DateTimeOffset ChangedAt, string Version)
{
    public static PriceListResponse From(PriceList list, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(list);
        return new(list.Id.Value, list.SiteId, list.CustomerId, list.Currency, list.EffectiveFrom, list.Status.ToString().ToLowerInvariant(),
            list.Status == PriceListStatus.Active && list.InForceBy(today), [.. list.Lines.Select(l => new PriceLineResponse(l.ProductId, l.UnitPrice))],
            list.CreatedAt, list.ChangedAt, RecordVersionHttp.ETag(list.Version));
    }
}

/// <summary>The price on a date: per <c>SaleUnit</c>, from <c>PriceListId</c>; <c>Source</c> "customer" or "site".</summary>
public sealed record PriceQuoteResponse(
    Guid ProductId, [property: Sensitive(SalesPermissions.UnitPriceField)] decimal? UnitPrice, string Currency, string SaleUnit, Guid PriceListId,
    DateOnly EffectiveFrom, string Source)
{
    public static PriceQuoteResponse From(PriceQuote quote)
    {
        ArgumentNullException.ThrowIfNull(quote);
        return new(quote.ProductId, quote.UnitPrice, quote.Currency, quote.SaleUnit, quote.PriceListId, quote.EffectiveFrom, quote.Source);
    }
}
