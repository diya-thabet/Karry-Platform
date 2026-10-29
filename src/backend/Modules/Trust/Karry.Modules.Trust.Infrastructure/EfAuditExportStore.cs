using Karry.Modules.Trust.Application;
using Karry.Modules.Trust.Domain;

namespace Karry.Modules.Trust.Infrastructure;

internal sealed class EfAuditExportStore(TrustDbContext db) : IAuditExportStore
{
    public async Task AddAsync(AuditExport export, CancellationToken cancellationToken)
    {
        db.Exports.Add(export);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false); // the export, its outbox row and its ledger entry
    }
}
