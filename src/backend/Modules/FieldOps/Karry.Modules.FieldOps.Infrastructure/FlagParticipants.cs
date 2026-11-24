using Karry.Modules.FieldOps.Application;
using Karry.Modules.Integrity.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Karry.Modules.FieldOps.Infrastructure;

/// <summary>
/// Whether a person signed one of the shift entries a flag is linked to (ADR-0037 §7): any version of the entry, so a
/// correction by someone else does not hide who drove. Row-level security keeps it in the company.
/// </summary>
internal sealed class FlagParticipants(FieldOpsDbContext db) : IFlagParticipants
{
    public async Task<bool> TookPartAsync(Guid person, IReadOnlyList<FlagSubject> related, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(related);
        var entries = related.Where(r => r.Type == IntegrityFindings.ShiftEntry).Select(r => r.Id).ToList();
        return entries.Count > 0
            && await db.ShiftEntries.AsNoTracking().AnyAsync(e => entries.Contains(e.RootId) && e.OperatorId == person, cancellationToken).ConfigureAwait(false);
    }
}
