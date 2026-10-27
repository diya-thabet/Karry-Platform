using Karry.Modules.Imports.Application;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.Modules.Imports.Infrastructure;

public static class ImportsInfrastructure
{
    /// <summary>The import kinds come from the modules that own the data (each registers its <c>IImportTarget</c>).</summary>
    public static IServiceCollection AddImportsInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IWorkbookReader, WorkbookReader>();
        services.AddSingleton<ITemplateWriter, TemplateWriter>();
        services.AddScoped<ImportTargets>();
        services.AddScoped<ImportTemplates>();
        return services;
    }
}
