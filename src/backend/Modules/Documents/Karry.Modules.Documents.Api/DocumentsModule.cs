using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Modules;
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

    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/shift-reports", ShiftReportAsync)
            .RequirePermission(DocumentsPermissions.ShiftReportsRead)
            .Produces(StatusCodes.Status200OK, contentType: "application/pdf")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("IssueShiftReport");

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
