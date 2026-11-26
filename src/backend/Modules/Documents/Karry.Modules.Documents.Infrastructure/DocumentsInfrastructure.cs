using Karry.Modules.Documents.Application;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.Modules.Documents.Infrastructure;

public static class DocumentsInfrastructure
{
    /// <summary>Documents (ADR-0040): the shift report's content; its envelope and storage arrive with slice 3.</summary>
    public static IServiceCollection AddDocumentsInfrastructure(this IServiceCollection services)
    {
        services.AddScoped<ShiftReportContents>();
        return services;
    }
}
