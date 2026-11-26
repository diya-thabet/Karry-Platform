using Karry.BuildingBlocks.Outbox;
using Karry.BuildingBlocks.Persistence;
using Karry.Modules.Documents.Application;
using Karry.Modules.Documents.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Karry.Modules.Documents.Infrastructure;

internal sealed class EnvelopeStore(DocumentsDbContext db) : IDocumentEnvelopeStore
{
    public void Add(DocumentEnvelope envelope) => db.Envelopes.Add(envelope);

    public Task SaveAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}

/// <summary>The shift report's PDF (ADR-0040 §1): PDFsharp + MigraDoc, Noto Sans embedded.</summary>
internal sealed class ShiftReportRenderer : IShiftReportRenderer
{
    public byte[] Render(Domain.ShiftReportContent content, Uri verification, IReadOnlyDictionary<string, byte[]> images, string language) =>
        ShiftReportPdf.Render(content, verification, images, language);
}

public static class DocumentsInfrastructure
{
    /// <summary>Documents (ADR-0040): the shift report's content, its PDF, and its envelope with the ledger entry.</summary>
    public static IServiceCollection AddDocumentsInfrastructure(this IServiceCollection services, string connectionString)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddModuleDbContext<DocumentsDbContext>(connectionString);
        services.AddOutboxDispatcher<DocumentsDbContext>(eventAssemblies: typeof(DocumentEnvelope).Assembly);
        services.AddOptions<DocumentsOptions>().BindConfiguration(DocumentsOptions.Section);
        services.AddScoped<ShiftReportContents>();
        services.AddScoped<IDocumentEnvelopeStore, EnvelopeStore>();
        services.AddSingleton<IShiftReportRenderer, ShiftReportRenderer>();
        services.AddScoped<ShiftReportIssuing>();
        return services;
    }
}
