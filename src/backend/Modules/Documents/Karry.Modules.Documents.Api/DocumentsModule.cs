using Karry.BuildingBlocks.Modules;
using Karry.Modules.Documents.Application;
using Karry.Modules.Documents.Infrastructure;
using Karry.SharedKernel.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.Modules.Documents.Api;

/// <summary>
/// Documents (ADR-0040): the signed shift report and its public verification. Routes under <c>/api/v1/documents</c>
/// arrive with the PDF (slice 3); this slice declares the permission and composes the report's content.
/// </summary>
public sealed class DocumentsModule : IModule
{
    public string Name => "documents";

    public IReadOnlyList<PermissionDefinition> Permissions => DocumentsPermissions.All;

    public void AddServices(IServiceCollection services, IConfiguration configuration) => services.AddDocumentsInfrastructure();

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
    }
}
