using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Idempotency;
using Karry.BuildingBlocks.Modules;
using Karry.Modules.Sales.Application;
using Karry.Modules.Sales.Domain;
using Karry.Modules.Sales.Infrastructure;
using Karry.SharedKernel.Authorization;
using Karry.SharedKernel.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.Modules.Sales.Api;

/// <summary>
/// The company's customers (US-P3-SAL-01, ADR-0042), and in the next slices its products and price lists. Routes under
/// <c>/api/v1/sales</c>.
/// </summary>
public sealed class SalesModule : IModule
{
    public string Name => "sales";

    public IReadOnlyList<PermissionDefinition> Permissions => SalesPermissions.All;

    public IReadOnlyList<SensitiveFieldDefinition> SensitiveFields => SalesPermissions.SensitiveFields;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var connectionString = configuration.GetConnectionString("Karry");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // Host without a database (some host tests): endpoints still map ([FromServices]) and fail only when called.
            return;
        }

        services.AddSalesInfrastructure(connectionString);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/customers", ListCustomersAsync)
            .RequirePermission(SalesPermissions.CustomersRead)
            .WithName("ListCustomers");

        endpoints.MapGet("/customers/{customerId:guid}", GetCustomerAsync)
            .RequirePermission(SalesPermissions.CustomersRead)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("GetCustomer");

        endpoints.MapPost("/customers", CreateCustomerAsync)
            .RequirePermission(SalesPermissions.CustomersManage)
            .RequireIdempotencyKey()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("CreateCustomer");

        endpoints.MapPut("/customers/{customerId:guid}", ChangeCustomerAsync)
            .RequirePermission(SalesPermissions.CustomersManage)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("ChangeCustomer");

        // US-P3-SAL-01 AC2: a blocked customer gets no new ticket; the reason is what the weighmaster reads.
        endpoints.MapPost("/customers/{customerId:guid}/block", BlockCustomerAsync)
            .RequirePermission(SalesPermissions.CustomersManage)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("BlockCustomer");

        endpoints.MapPost("/customers/{customerId:guid}/unblock", UnblockCustomerAsync)
            .RequirePermission(SalesPermissions.CustomersManage)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("UnblockCustomer");
    }

    private static async Task<Ok<CustomerListResponse>> ListCustomersAsync(
        [FromServices] CustomerOperations customers, CancellationToken cancellationToken, string? status = null, string? search = null)
    {
        var list = await customers.ListAsync(status, search, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(new CustomerListResponse([.. list.Select(CustomerResponse.From)]));
    }

    private static async Task<Results<Ok<CustomerResponse>, ProblemHttpResult>> GetCustomerAsync(
        Guid customerId, HttpResponse response, [FromServices] CustomerOperations customers, CancellationToken cancellationToken) =>
        Respond(await customers.GetAsync(new CustomerId(customerId), cancellationToken).ConfigureAwait(false), response);

    private static async Task<Results<Created<CustomerResponse>, ProblemHttpResult>> CreateCustomerAsync(
        CustomerInput request, [FromServices] CustomerOperations customers, CancellationToken cancellationToken)
    {
        var result = await customers.CreateAsync(request, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? TypedResults.Created($"/api/v1/sales/customers/{result.Value.Id}", CustomerResponse.From(result.Value))
            : result.Error.ToProblem();
    }

    /// <summary><c>If-Match</c>: the <c>ETag</c> (or <c>version</c>) the form was filled from; a newer customer is a 409 (H-C1).</summary>
    private static async Task<Results<Ok<CustomerResponse>, ProblemHttpResult>> ChangeCustomerAsync(
        Guid customerId, CustomerInput request, [FromHeader(Name = "If-Match")] string? ifMatch, HttpResponse response,
        [FromServices] CustomerOperations customers, CancellationToken cancellationToken)
    {
        var expected = RecordVersionHttp.Expected(ifMatch);
        return expected.IsSuccess
            ? Respond(await customers.ChangeAsync(new CustomerId(customerId), request, cancellationToken, expected.Value).ConfigureAwait(false), response)
            : expected.Error.ToProblem();
    }

    private static async Task<Results<Ok<CustomerResponse>, ProblemHttpResult>> BlockCustomerAsync(
        Guid customerId, BlockInput request, HttpResponse response, [FromServices] CustomerOperations customers, CancellationToken cancellationToken) =>
        Respond(await customers.BlockAsync(new CustomerId(customerId), request, cancellationToken).ConfigureAwait(false), response);

    private static async Task<Results<Ok<CustomerResponse>, ProblemHttpResult>> UnblockCustomerAsync(
        Guid customerId, HttpResponse response, [FromServices] CustomerOperations customers, CancellationToken cancellationToken) =>
        Respond(await customers.UnblockAsync(new CustomerId(customerId), cancellationToken).ConfigureAwait(false), response);

    private static Results<Ok<CustomerResponse>, ProblemHttpResult> Respond(Result<Customer> result, HttpResponse response)
    {
        if (!result.IsSuccess)
        {
            return result.Error.ToProblem();
        }

        response.SetETag(result.Value.Version);
        return TypedResults.Ok(CustomerResponse.From(result.Value));
    }
}

public sealed record CustomerListResponse(IReadOnlyList<CustomerResponse> Items);

public sealed record MoneyResponse(decimal Amount, string Currency);

/// <summary>
/// A customer. <c>AllowedSiteIds</c> empty means every site; <c>CreditLimit</c> is the sensitive field
/// <c>sales.customers.credit-limit</c> (absent and listed in <c>maskedFields</c> for roles that may not read it).
/// </summary>
public sealed record CustomerResponse(
    Guid Id, string Code, string LegalName, string? TaxId, IReadOnlyList<CustomerAddress> Addresses, IReadOnlyList<CustomerContact> Contacts,
    int PaymentTermsDays, string Basis, IReadOnlyList<Guid> AllowedSiteIds,
    [property: Sensitive(SalesPermissions.CreditLimitField)] MoneyResponse? CreditLimit,
    string Status, string? BlockedReason, DateTimeOffset CreatedAt, DateTimeOffset ChangedAt, string Version)
{
    public static CustomerResponse From(Customer customer)
    {
        ArgumentNullException.ThrowIfNull(customer);
        return new(customer.Id.Value, customer.Code, customer.LegalName, customer.TaxId, customer.Addresses, customer.Contacts,
            customer.PaymentTermsDays, CustomerRules.BasisCode(customer.Basis), customer.AllowedSiteIds,
            customer.CreditLimit is { } limit ? new MoneyResponse(limit.Amount, limit.Currency.Code) : null,
            customer.Status.ToString().ToLowerInvariant(), customer.BlockedReason, customer.CreatedAt, customer.ChangedAt,
            RecordVersionHttp.ETag(customer.Version));
    }
}
