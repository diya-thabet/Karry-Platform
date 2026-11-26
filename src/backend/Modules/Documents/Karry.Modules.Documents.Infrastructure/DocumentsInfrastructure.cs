using Karry.BuildingBlocks.Outbox;
using Karry.BuildingBlocks.Persistence;
using Karry.Modules.Documents.Application;
using Karry.Modules.Documents.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Karry.Modules.Documents.Infrastructure;

internal sealed class EnvelopeStore(DocumentsDbContext db) : IDocumentEnvelopeStore, IEnvelopeReader, IVerificationKeys
{
    /// <summary>The envelope and its verification key, saved in the same transaction.</summary>
    public void Add(DocumentEnvelope envelope)
    {
        db.Envelopes.Add(envelope);
        db.VerificationKeys.Add(VerificationKey.Of(envelope));
    }

    public Task<DocumentEnvelope?> FindAsync(Guid envelopeId, CancellationToken cancellationToken) =>
        db.Envelopes.FirstOrDefaultAsync(e => e.Id == new DocumentEnvelopeId(envelopeId), cancellationToken);

    Task<VerificationKey?> IVerificationKeys.FindAsync(Guid envelopeId, CancellationToken cancellationToken) =>
        db.VerificationKeys.AsNoTracking().FirstOrDefaultAsync(k => k.EnvelopeId == envelopeId, cancellationToken);

    public async Task<IReadOnlyList<DocumentEnvelope>> UnsignedAsync(int take, CancellationToken cancellationToken) =>
        await db.Envelopes.Where(e => e.SignedAt == null).OrderBy(e => e.IssuedAt).Take(take).ToListAsync(cancellationToken).ConfigureAwait(false);

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
        services.AddScoped<EnvelopeStore>();
        services.AddScoped<IDocumentEnvelopeStore>(sp => sp.GetRequiredService<EnvelopeStore>());
        services.AddScoped<IEnvelopeReader>(sp => sp.GetRequiredService<EnvelopeStore>());
        services.AddScoped<IVerificationKeys>(sp => sp.GetRequiredService<EnvelopeStore>());
        services.AddScoped<DocumentVerification>();
        services.AddSingleton<IShiftReportRenderer, ShiftReportRenderer>();
        services.AddScoped<ShiftReportIssuing>();
        return services;
    }
}
