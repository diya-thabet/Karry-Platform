using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Modules;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Documents.Application;
using Karry.Modules.Documents.Infrastructure;
using Karry.SharedKernel.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.Modules.Documents.Api;

/// <summary>
/// Documents (ADR-0040): the signed shift report and its public verification. Routes under <c>/api/v1/documents</c>.
/// </summary>
public sealed class DocumentsModule : IModule
{
    public const string EnvelopeHeader = "X-Karry-Envelope";

    public string Name => "documents";

    public IReadOnlyList<PermissionDefinition> Permissions => DocumentsPermissions.All;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var connectionString = configuration.GetConnectionString("Karry");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // Host without a database (some host tests): endpoints still map ([FromServices]) and fail only when called.
            return;
        }

        services.AddDocumentsInfrastructure(connectionString);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/shift-reports", ShiftReportAsync)
            .RequirePermission(DocumentsPermissions.ShiftReportsRead)
            .Produces(StatusCodes.Status200OK, contentType: "application/pdf")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("IssueShiftReport");

        // Public (ADR-0040 §4): the QR's page posts the token from its fragment. Anonymous callers are limited per address
        // (RateLimits:AnonymousPerAddressPerMinute); the ID is a plain segment so a malformed one is just another miss.
        endpoints.MapPost("/verify/{envelopeId}", VerifyAsync)
            .AllowAnonymous()
            .AllowWithoutTenant()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .WithName("VerifyDocument");
    }

    /// <summary>
    /// Checks a document (US-P2-SIG-06, TC-P2-SIG-005): the envelope ID and its token give what the document is, whose,
    /// its hashes and the server's signature; with <c>pdfHash</c> (computed in the browser), whether that PDF is the one
    /// issued (TC-P2-DOC-002). Every miss is the same 404 <c>documents.not_found</c>.
    /// </summary>
    private static async Task<Results<Ok<VerifiedDocument>, ProblemHttpResult>> VerifyAsync(
        string envelopeId, VerifyDocumentRequest? request, HttpResponse response, [FromServices] DocumentVerification verification,
        CancellationToken cancellationToken)
    {
        response.Headers.CacheControl = "no-store";
        var result = await verification.VerifyAsync(envelopeId, request?.Token, request?.PdfHash, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    /// <summary>
    /// Issues a site's shift report as a PDF (US-P2-SIG-05): the caller's view of the entries, the content hash in the
    /// footer and a QR to its public verification. Each call issues a new envelope (header <c>X-Karry-Envelope</c>),
    /// ledgered with the content and PDF hashes. <c>language</c>: <c>fr</c> (default) or <c>en</c>.
    /// </summary>
    private static async Task<Results<FileContentHttpResult, ProblemHttpResult>> ShiftReportAsync(
        Guid siteId, DateOnly date, string? shift, string? language, HttpResponse response,
        [FromServices] ShiftReportIssuing issuing, CancellationToken cancellationToken)
    {
        var result = await issuing.IssueAsync(siteId, date, shift, language, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return result.Error.ToProblem();
        }

        response.Headers[EnvelopeHeader] = result.Value.EnvelopeId.ToString("D");
        response.Headers.CacheControl = "no-store";
        return TypedResults.File(result.Value.Pdf, "application/pdf", $"shift-report-{date:yyyy-MM-dd}-{shift}.pdf");
    }
}

/// <summary>The token from the QR's fragment and, optionally, the SHA-256 of a PDF the visitor dropped on the page.</summary>
public sealed record VerifyDocumentRequest(string? Token, string? PdfHash);
