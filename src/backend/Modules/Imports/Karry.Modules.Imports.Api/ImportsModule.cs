using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Modules;
using Karry.Modules.Imports.Application;
using Karry.Modules.Imports.Infrastructure;
using Karry.SharedKernel.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.Modules.Imports.Api;

/// <summary>
/// Loading a company's data from Excel (US-P1-ORG-03, ADR-0027): templates per kind, then upload, validation report,
/// preview and commit. Routes under <c>/api/v1/imports</c>.
/// </summary>
public sealed class ImportsModule : IModule
{
    public const string WorkbookContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public string Name => "imports";

    public IReadOnlyList<PermissionDefinition> Permissions => ImportsPermissions.All;

    public void AddServices(IServiceCollection services, IConfiguration configuration) => services.AddImportsInfrastructure();

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/templates/{kind}", TemplateAsync)
            .RequirePermission(ImportsPermissions.FilesManage)
            .Produces(StatusCodes.Status200OK, contentType: WorkbookContentType)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("DownloadImportTemplate");
    }

    /// <summary>The template of a kind (<c>sites</c>, <c>assets</c>, <c>meters</c>, <c>materials</c>, <c>users</c>) in French or English.</summary>
    private static async Task<IResult> TemplateAsync(string kind, string? language, [FromServices] ImportTemplates templates, CancellationToken cancellationToken)
    {
        var template = await templates.TemplateAsync(kind, language, cancellationToken).ConfigureAwait(false);
        return template.IsSuccess
            ? Results.File(template.Value, WorkbookContentType, $"karry-{kind}-{language ?? "fr"}.xlsx")
            : template.Error.ToProblem();
    }
}
