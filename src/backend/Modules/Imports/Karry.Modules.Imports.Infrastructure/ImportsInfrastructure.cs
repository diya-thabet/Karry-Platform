using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Outbox;
using Karry.BuildingBlocks.Persistence;
using Karry.BuildingBlocks.Storage;
using Karry.Modules.Imports.Application;
using Karry.Modules.Imports.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Karry.Modules.Imports.Infrastructure;

public static class ImportsInfrastructure
{
    /// <summary>The import kinds come from the modules that own the data (each registers its <c>IImportTarget</c>).</summary>
    public static IServiceCollection AddImportsInfrastructure(this IServiceCollection services)
    {
        services.TryAddSingleton<IWorkbookReader, WorkbookReader>();
        services.TryAddSingleton<ITemplateWriter, TemplateWriter>();
        services.TryAddScoped<ImportTargets>();
        services.TryAddScoped<ImportTemplates>();
        return services;
    }

    /// <summary>
    /// Import jobs (ADR-0027 §4): storage, files in object storage, the API operations, and the runner that the module's
    /// outbox calls in the worker (validation and commit), the retention run and the malware scanner port.
    /// </summary>
    public static IServiceCollection AddImportsInfrastructure(this IServiceCollection services, string connectionString, IConfiguration configuration)
    {
        services.AddImportsInfrastructure();
        services.AddObjectStore(configuration);
        services.TryAddSingleton(TimeProvider.System);
        services.AddKarryAuthorization();
        services.AddModuleDbContext<ImportsDbContext>(connectionString);
        services.AddOutboxDispatcher<ImportsDbContext>(eventAssemblies: typeof(ImportJob).Assembly);
        services.AddScoped<IImportJobStore, ImportJobStore>();
        services.AddScoped<ImportJobOperations>();
        services.TryAddSingleton<IFileScanner, NoFileScanner>();
        services.AddScoped<ImportRunner>();
        services.AddScoped<IDomainEventSubscriber<ImportUploaded>>(sp => sp.GetRequiredService<ImportRunner>());
        services.AddScoped<IDomainEventSubscriber<ImportCommitRequested>>(sp => sp.GetRequiredService<ImportRunner>());
        services.AddSingleton<DeleteExpiredImportFiles>();
        return services;
    }
}
